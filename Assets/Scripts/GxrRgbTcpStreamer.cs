using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using GxrSdk;
using UnityEngine;

// -----------------------------------------------------------------------------
// GxrRgbTcpStreamer（Unity 端 RGB 相机帧 TCP 推流器）
//
// 作用：
// - 在设备端（眼镜/Android）通过 GxrSdk 获取 RGB 相机帧；
// - 将每帧 Texture2D 编码为 JPEG；
// - 通过 TCP 发送到 PC 端接收器（Tools/PcReceiver/Program.cs）。
//
// 设计要点：
// - 相机回调线程/时机不确定：在回调中只做“尽量少”的工作（编码 + 入队），把网络发送放到后台线程。
// - 使用 ConcurrentQueue 做线程安全的生产者/消费者队列：回调线程生产，SenderThread 消费。
// - 有背压（MaxPendingBytes）：当接收端断开或网络拥塞时，丢帧而不是无限堆积导致 OOM。
//
// 与 PC 端一致的协议（Little-Endian，小端）：
// - Header 固定 28 字节：
//   Magic(4)='GXR1' + Version(1)=1 + Reserved(3) +
//   Timestamp(uint64) + Width(int32) + Height(int32) + PayloadLength(int32)
// - Payload：紧跟 JPEG 原始字节流
//
// 字段偏移（相对 header 起始）：
// - 0..3   : Magic
// - 4      : Version
// - 5..7   : Reserved（当前写 0）
// - 8..15  : Timestamp（uint64，小端）
// - 16..19 : Width（int32，小端）
// - 20..23 : Height（int32，小端）
// - 24..27 : PayloadLength（int32，小端）
// -----------------------------------------------------------------------------

public sealed class GxrRgbTcpStreamer : MonoBehaviour, IGxrRgbCameraFrameHandler
{
    [Header("PC Receiver")]
    // PC 的 IP/端口：需要与 PcReceiver 监听的地址/端口一致。
    // 典型用法：PC 与设备在同一局域网，pcIp 填 PC 的局域网 IP（如 192.168.x.x）。
    [SerializeField] private string pcIp = "10.1.52.169";
    [SerializeField] private int pcPort = 5001;

    [Header("Encoding")]
    // JPEG 压缩质量：越大画质越好、体积越大、编码耗时越高。
    [Range(1, 100)]
    [SerializeField] private int jpegQuality = 70;
    // 丢帧策略：0 表示不跳帧；2 表示“发 1 帧 + 跳过 2 帧”（等价于降低发送/编码频率）。
    // 注意：这里的“跳帧”发生在回调处，意味着编码也会减少。
    [Tooltip("0 = send every frame; 2 = send one frame then skip 2 frames.")]
    [SerializeField] private int skipFrames = 0;

    [Header("Diagnostics")]
    // 是否输出更详细的日志（每帧发送成功都会打日志，可能影响性能）。
    [SerializeField] private bool verboseLog = false;

    // TCP 连接对象与其网络流：
    // - _client 用于管理连接；
    // - _stream 用于读写字节（这里只写）。
    private TcpClient _client;
    private NetworkStream _stream;
    // 后台发送线程：从队列取 Packet 并写入 TCP。
    private Thread _senderThread;
    // 线程取消标记：用于通知 SenderLoop 退出。
    private CancellationTokenSource _cts;

    // 线程安全队列：回调线程入队，SenderLoop 出队。
    private readonly ConcurrentQueue<Packet> _queue = new ConcurrentQueue<Packet>();
    // 当前队列内 JPEG 的累计字节数，用于背压判断（避免无限堆积）。
    private int _pendingBytes;
    // 用于 skipFrames 的帧计数器（在回调里递增）。
    private int _frameCounter;

    // Guardrail to avoid OOM if receiver disconnects or can't keep up.
    // “最大待发送字节数”阈值：
    // - 如果 PC 端断开/卡顿，队列会增长；
    // - 达到阈值后直接丢弃新帧，保护内存。
    private const int MaxPendingBytes = 8 * 1024 * 1024;

    // 4 字节魔数：用于接收端快速验证协议同步。
    private static readonly byte[] Magic = { (byte)'G', (byte)'X', (byte)'R', (byte)'1' };

    // 队列中的最小消息单元：
    // - 只包含发送端需要的元信息（时间戳、尺寸）和 JPEG 字节数组。
    private struct Packet
    {
        public ulong Timestamp;
        public int Width;
        public int Height;
        public byte[] Jpeg;
    }

    private void Start()
    {
        // Ensure RGB camera is enabled on glasses.
        // If your project already controls these elsewhere, you can remove these calls.
        try
        {
            // 启用 RGB 相机（具体行为由 GxrSdk 实现，可能涉及系统权限/硬件初始化等）。
            GxrCameraSystem.EnableRgbCamera();
            // Preview is optional; it does NOT affect whether frames are delivered.
            // GxrCameraSystem.EnableRgbCameraPreview();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GxrRgbTcpStreamer] EnableRgbCamera failed: {e.Message}");
        }

        // 启动 TCP 连接与发送线程。
        StartSender();
    }

    private void OnEnable()
    {
        // 注册 RGB 帧回调：当相机有新帧时会调用 OnRgbCameraFrameUpdated。
        GxrSystemAccessor.CameraSystem?.RegisterHandler<IGxrRgbCameraFrameHandler>(this);
    }

    private void OnDisable()
    {
        // 取消注册：避免对象禁用后仍收到回调。
        GxrSystemAccessor.CameraSystem?.UnregisterHandler<IGxrRgbCameraFrameHandler>(this);
    }

    private void OnDestroy()
    {
        // Unity 对象销毁时确保释放网络资源与线程。
        StopSender();
    }

    public void OnRgbCameraFrameUpdated(GxrCameraFrameEventData eventData)
    {
        // 若已停止/未连接，直接返回，避免在异常状态下继续入队。
        if (_cts == null || _cts.IsCancellationRequested) return;
        if (_stream == null) return;
        if (_client == null || !_client.Connected) return;

        if (skipFrames > 0)
        {
            // 简单的跳帧实现：
            // - c 从 0 开始；
            // - 每 (skipFrames + 1) 帧发送一次，其余直接丢弃。
            var c = _frameCounter++;
            if (c % (skipFrames + 1) != 0) return;
        }

        // eventData.FrameInfo 由 SDK 提供，包含时间戳、尺寸和 Texture 等信息。
        var frame = eventData.FrameInfo;
        if (frame.FrameTexture == null) return;

        // Backpressure: drop frames if sender can't keep up.
        // 这里读取 _pendingBytes 使用 Volatile，保证跨线程读到较新的值。
        if (Volatile.Read(ref _pendingBytes) > MaxPendingBytes) return;

        byte[] jpg;
        try
        {
            // EncodeToJPG 是 Unity 的纹理编码接口：
            // - 会产生新的 byte[]（由 GC 管理）；
            // - 编码可能抛异常（例如纹理格式不支持、资源状态异常等）。
            jpg = frame.FrameTexture.EncodeToJPG(jpegQuality);
        }
        catch (Exception e)
        {
            if (verboseLog) Debug.LogWarning($"[GxrRgbTcpStreamer] EncodeToJPG failed: {e.Message}");
            return;
        }

        if (jpg == null || jpg.Length == 0) return;

        // 在入队前先增加 pending 计数，随后 SenderLoop 在 finally 中扣回。
        // 这样即便发送过程中异常，也能确保计数正确回收。
        Interlocked.Add(ref _pendingBytes, jpg.Length);
        _queue.Enqueue(new Packet
        {
            Timestamp = frame.Timestamp,
            Width = frame.Width,
            Height = frame.Height,
            Jpeg = jpg
        });
    }

    private void StartSender()
    {
        // 先确保处于“干净状态”（释放旧连接/线程，并清空队列）。
        StopSender();

        // 每次 StartSender 都创建新的取消源，供 SenderLoop 使用。
        _cts = new CancellationTokenSource();

        try
        {
            // 建立 TCP 连接：这里是阻塞 Connect；若要避免卡主主线程，可以改异步/协程（此处保持简单）。
            _client = new TcpClient();
            _client.NoDelay = true;
            _client.Connect(pcIp, pcPort);
            _stream = _client.GetStream();
        }
        catch (Exception e)
        {
            Debug.LogError($"[GxrRgbTcpStreamer] TCP connect {pcIp}:{pcPort} failed: {e.Message}");
            StopSender();
            return;
        }

        // 后台线程：专职把队列中的 JPEG 帧写到 TCP。
        _senderThread = new Thread(SenderLoop)
        {
            IsBackground = true,
            Name = "GxrRgbTcpStreamer.Sender"
        };
        _senderThread.Start();
    }

    private void StopSender()
    {
        // 按“通知 -> 等待 -> 释放资源”的顺序停掉发送线程。
        try { _cts?.Cancel(); } catch { /* ignore */ }

        // 等待发送线程退出一小段时间，避免 Unity 关闭时卡住。
        try { _senderThread?.Join(500); } catch { /* ignore */ }
        _senderThread = null;

        // 释放网络流/连接对象（Close 会释放底层 socket）。
        try { _stream?.Close(); } catch { /* ignore */ }
        _stream = null;

        try { _client?.Close(); } catch { /* ignore */ }
        _client = null;

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;

        // 清空队列并回收 pending 统计（确保下次 StartSender 时状态干净）。
        while (_queue.TryDequeue(out var p))
        {
            if (p.Jpeg != null) Interlocked.Add(ref _pendingBytes, -p.Jpeg.Length);
        }

        _pendingBytes = 0;
        _frameCounter = 0;
    }

    private void SenderLoop()
    {
        try
        {
            // Packet format (little-endian):
            // Magic(4) + Version(1) + Reserved(3) +
            // Timestamp(uint64) + Width(int32) + Height(int32) +
            // PayloadLength(int32) + Payload(JPEG bytes)
            // header 复用同一个 byte[]，避免每帧都分配新数组造成 GC 压力。
            var header = new byte[4 + 1 + 3 + 8 + 4 + 4 + 4];

            while (!_cts.IsCancellationRequested)
            {
                if (!_queue.TryDequeue(out var packet))
                {
                    // 队列空时稍作休眠，避免空转占用 CPU。
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    // 组装 header：Magic + Version + Reserved(0)
                    Buffer.BlockCopy(Magic, 0, header, 0, 4);
                    header[4] = 1; // version
                    header[5] = 0;
                    header[6] = 0;
                    header[7] = 0;

                    // 写入元信息（全部小端）并写入 JPEG payload。
                    WriteU64LE(header, 8, packet.Timestamp);
                    WriteI32LE(header, 16, packet.Width);
                    WriteI32LE(header, 20, packet.Height);
                    WriteI32LE(header, 24, packet.Jpeg.Length);

                    // 注意：NetworkStream.Write 并不保证对端立即接收，只保证把数据写入 socket 缓冲。
                    // 这里 Flush 只是让 stream 层尽快提交（对 NetworkStream 来说通常是 no-op，但保留无害）。
                    _stream.Write(header, 0, header.Length);
                    _stream.Write(packet.Jpeg, 0, packet.Jpeg.Length);
                    _stream.Flush();

                    if (verboseLog)
                    {
                        Debug.Log($"[GxrRgbTcpStreamer] sent ts={packet.Timestamp} {packet.Width}x{packet.Height} bytes={packet.Jpeg.Length}");
                    }
                }
                finally
                {
                    // 无论发送成功/失败，都把该帧的 byte 数从 pending 中扣回，避免计数泄漏。
                    Interlocked.Add(ref _pendingBytes, -packet.Jpeg.Length);
                }
            }
        }
        catch (Exception e)
        {
            // 任何异常都会导致发送线程退出（最小实现）。
            // 如果你希望“断线自动重连”，可以在这里触发 StartSender 重试或做状态上报。
            Debug.LogError($"[GxrRgbTcpStreamer] SenderLoop crashed: {e.Message}");
        }
    }

    private static void WriteI32LE(byte[] buffer, int offset, int value)
    {
        // 以小端方式写入 int32 到 buffer。
        buffer[offset + 0] = (byte)(value);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteU64LE(byte[] buffer, int offset, ulong value)
    {
        // 以小端方式写入 uint64 到 buffer。
        buffer[offset + 0] = (byte)(value);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
        buffer[offset + 4] = (byte)(value >> 32);
        buffer[offset + 5] = (byte)(value >> 40);
        buffer[offset + 6] = (byte)(value >> 48);
        buffer[offset + 7] = (byte)(value >> 56);
    }
}
