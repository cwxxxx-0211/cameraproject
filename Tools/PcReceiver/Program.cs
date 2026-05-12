using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenCvSharp;

// -----------------------------------------------------------------------------
// PcReceiver（PC 端最小可用接收器）
//
// 作用：
// - 作为 Unity 端 `GxrRgbTcpStreamer` 的配套接收端，在 PC 上监听一个 TCP 端口；
// - 按约定协议接收一帧一帧的 JPEG 数据；
// - 直接在内存中解码 JPEG 并弹窗实时显示（不再写 latest.jpg）。
//
// 用法：
// - 不带参数：监听 5001，并打开 OpenCV 窗口显示画面
// - 参数 1：端口号，例如：PcReceiver.exe 5001
//
// 注意：
// - 这是“最小可用”的接收器：一次只处理一个客户端连接；断开后继续等待下一个连接。
// - TCP 是字节流协议：一次 Read 不一定读满你想要的字节数，所以必须循环读取直到满足长度。
// - 若把 ImShow/解码放在网络读循环中，会拖慢 ReadExactly，导致 TCP 缓冲堆积、延迟越来越高。
//   因此这里采用“生产者-消费者”模型：
//   - 网络接收线程（生产者）：只负责 ReadExactly + 把 payload 入队；
//   - UI/渲染线程（消费者，主线程）：从队列取最新帧，丢弃积压旧帧，解码并 ImShow。
//
// OpenCV 依赖：
// - 需要在此 PC 工程（Tools/PcReceiver）中安装 OpenCvSharp4 与 OpenCvSharp4.runtime.win
//  （通过 Visual Studio 的 NuGet 管理器或直接编辑 csproj）。
//
// 传输协议（Little-Endian，小端；与 Unity 端保持一致）：
// - Header 固定长度 28 字节：
//   Magic(4)='GXR1' + Version(1)=1 + Reserved(3) +
//   Timestamp(uint64) + Width(int32) + Height(int32) + PayloadLength(int32)
// - 随后紧跟 PayloadLength 字节的 Payload（JPEG 原始字节流）
//
// 字段偏移（相对 header 起始）：
// - 0..3   : Magic
// - 4      : Version
// - 5..7   : Reserved（预留，当前写 0）
// - 8..15  : Timestamp（uint64，小端）
// - 16..19 : Width（int32，小端）
// - 20..23 : Height（int32，小端）
// - 24..27 : PayloadLength（int32，小端）
// -----------------------------------------------------------------------------

static class Program
{
    // 与发送端约定的 4 字节魔数（magic number），用于快速识别协议并过滤无效数据流。
    private static readonly byte[] Magic = { (byte)'G', (byte)'X', (byte)'R', (byte)'1' };

    static int Main(string[] args)
    {
        // 解析命令行参数：端口号。
        // 这里使用 int.Parse：如果传入非法端口会抛异常并终止程序（符合“最小可用”定位）。
        var port = args.Length >= 1 ? int.Parse(args[0]) : 5001;
        var windowName = "AR Glasses Real-Time Stream";

        Console.WriteLine($"[PcReceiver] listen :{port}, window=\"{windowName}\"");

        // 监听任意网卡（IPAddress.Any）上的指定端口。
        // 如需只允许本机连接，可改为 IPAddress.Loopback（但这里按“最小可用”保持简单）。
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        while (true)
        {
            Console.WriteLine("[PcReceiver] waiting client...");
            // AcceptTcpClient 是阻塞调用：直到有客户端连接才会返回。
            // using var：在循环结束或异常时确保连接释放。
            using var client = listener.AcceptTcpClient();
            // 关闭 Nagle：让小包尽快发出，减少延迟（对实时帧流更友好）。
            client.NoDelay = true;
            Console.WriteLine($"[PcReceiver] connected: {client.Client.RemoteEndPoint}");

            try
            {
                using var stream = client.GetStream();
                // 单连接会话：持续读取并处理帧，直到断开或出错。
                RunSession(stream, windowName);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[PcReceiver] session error: {e.Message}");
            }

            Console.WriteLine("[PcReceiver] disconnected.");
        }
    }

    private static void RunSession(NetworkStream stream, string windowName)
    {
        // 用一个线程安全队列传递“收到的 JPEG payload”（生产者-消费者）。
        // 注意：为了降低延迟，消费者会主动丢弃积压，只取“最新的一帧”。
        var queue = new ConcurrentQueue<byte[]>();
        using var cts = new CancellationTokenSource();

        // 固定头部长度（与发送端一致）：
        // 4(Magic) + 1(Version) + 3(Reserved) + 8(Timestamp) + 4(Width) + 4(Height) + 4(PayloadLength) = 28
        var header = new byte[4 + 1 + 3 + 8 + 4 + 4 + 4];

        // 接收端诊断：统计“收到的帧率”（生产者侧）与“显示的帧率”（消费者侧）。
        var recvSw = Stopwatch.StartNew();
        var showSw = Stopwatch.StartNew();
        var recvCount = 0;
        var showCount = 0;
        var lastMeta = (timestamp: 0UL, width: 0, height: 0, bytes: 0);

        // 生产者：只负责从网络读完整协议，并把 payload 入队；不要做解码/显示。
        var receiverThread = new Thread(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    ReadExactly(stream, header, 0, header.Length);

                    if (header[0] != Magic[0] || header[1] != Magic[1] || header[2] != Magic[2] || header[3] != Magic[3])
                        throw new InvalidDataException("bad magic");

                    var version = header[4];
                    if (version != 1)
                        throw new InvalidDataException($"unsupported version: {version}");

                    var timestamp = ReadU64LE(header, 8);
                    var width = ReadI32LE(header, 16);
                    var height = ReadI32LE(header, 20);
                    var payloadLen = ReadI32LE(header, 24);

                    if (payloadLen <= 0 || payloadLen > 50 * 1024 * 1024)
                        throw new InvalidDataException($"bad payloadLen: {payloadLen}");

                    var payload = new byte[payloadLen];
                    ReadExactly(stream, payload, 0, payloadLen);

                    queue.Enqueue(payload);
                    recvCount++;
                    lastMeta = (timestamp, width, height, payloadLen);
                }
            }
            catch (Exception e)
            {
                // 任何异常（断线、协议错误等）都通知主线程退出消费循环并清理窗口。
                Console.WriteLine($"[PcReceiver] recv thread error: {e.Message}");
                try { cts.Cancel(); } catch { /* ignore */ }
            }
        })
        {
            IsBackground = true,
            Name = "PcReceiver.NetworkReceiver"
        };
        receiverThread.Start();

        // 消费者/UI：从队列取数据，“只保留最新帧”，再用 OpenCV 解码并显示。
        // OpenCV 的 HighGUI（ImShow/WaitKey）需要持续调用 WaitKey 来处理窗口消息，否则窗口可能不刷新/无响应。
        try
        {
            Cv2.NamedWindow(windowName, WindowFlags.AutoSize);

            while (!cts.IsCancellationRequested)
            {
                // 取最新帧：先拿到一帧作为候选，然后把队列里积压的都丢掉，只保留最后一个。
                if (!queue.TryDequeue(out var payload))
                {
                    // 没帧也要调用 WaitKey 处理窗口事件；同时让出一点 CPU。
                    Cv2.WaitKey(1);
                    Thread.Sleep(1);
                    continue;
                }

                while (queue.TryDequeue(out var newer))
                {
                    payload = newer;
                }

                // 在内存中解码 JPEG -> Mat，并显示。
                // ImDecode 会分配 Mat 内部内存；用 using 及时释放，避免长时间运行内存上涨。
                using var frame = Cv2.ImDecode(payload, ImreadModes.Color);
                if (!frame.Empty())
                {
                    Cv2.ImShow(windowName, frame);
                }

                // 1ms 轮询，既刷新 UI 也能捕获按键（例如窗口聚焦时按 ESC 退出）。
                // 约定：按 ESC 退出当前会话（不退出整个进程，会回到等待下一个 client）。
                var key = Cv2.WaitKey(1);
                if (key == 27) // ESC
                {
                    Console.WriteLine("[PcReceiver] ESC pressed, closing session...");
                    break;
                }

                showCount++;

                // 每秒输出一次诊断信息（接收 fps / 显示 fps / 最后一帧信息）。
                if (recvSw.ElapsedMilliseconds >= 1000 || showSw.ElapsedMilliseconds >= 1000)
                {
                    var recvFps = recvCount * 1000.0 / Math.Max(1, recvSw.ElapsedMilliseconds);
                    var showFps = showCount * 1000.0 / Math.Max(1, showSw.ElapsedMilliseconds);
                    Console.WriteLine($"[PcReceiver] recv_fps={recvFps:F1} show_fps={showFps:F1} last={lastMeta.width}x{lastMeta.height} bytes={lastMeta.bytes} ts={lastMeta.timestamp}");
                    recvCount = 0;
                    showCount = 0;
                    recvSw.Restart();
                    showSw.Restart();
                }
            }
        }
        finally
        {
            // 退出会话：通知接收线程停止，关闭 OpenCV 窗口。
            try { cts.Cancel(); } catch { /* ignore */ }
            try { receiverThread.Join(500); } catch { /* ignore */ }
            try { Cv2.DestroyWindow(windowName); } catch { /* ignore */ }
        }
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer, int offset, int count)
    {
        // TCP 的 Read 可能“读到就返回”，读到的字节数 n 可能小于请求的 count。
        // 因此必须循环读取直到读满，才能保证按协议边界解析不出错。
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, offset + read, count - read);
            // n==0 通常表示对端关闭连接；这里转成 EndOfStreamException 让上层统一处理。
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static int ReadI32LE(byte[] buffer, int offset)
    {
        // 以小端方式把 4 字节拼成 int32：
        // byte0 是最低有效字节（LSB），byte3 是最高有效字节（MSB）。
        return buffer[offset + 0]
               | (buffer[offset + 1] << 8)
               | (buffer[offset + 2] << 16)
               | (buffer[offset + 3] << 24);
    }

    private static ulong ReadU64LE(byte[] buffer, int offset)
    {
        // 以小端方式把 8 字节拼成 uint64。
        return (ulong)buffer[offset + 0]
               | ((ulong)buffer[offset + 1] << 8)
               | ((ulong)buffer[offset + 2] << 16)
               | ((ulong)buffer[offset + 3] << 24)
               | ((ulong)buffer[offset + 4] << 32)
               | ((ulong)buffer[offset + 5] << 40)
               | ((ulong)buffer[offset + 6] << 48)
               | ((ulong)buffer[offset + 7] << 56);
    }
}
