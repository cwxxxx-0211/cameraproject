using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

// -----------------------------------------------------------------------------
// PcReceiver（PC 端最小可用接收器）
//
// 作用：
// - 作为 Unity 端 `GxrRgbTcpStreamer` 的配套接收端，在 PC 上监听一个 TCP 端口；
// - 按约定协议接收一帧一帧的 JPEG 数据；
// - 把“最新的一帧”写到磁盘文件（默认 latest.jpg），方便你用任意工具查看/处理。
//
// 用法：
// - 不带参数：监听 5001，输出 latest.jpg
// - 参数 1：端口号，例如：PcReceiver.exe 5001
// - 参数 2：输出路径，例如：PcReceiver.exe 5001 D:\temp\latest.jpg
//
// 注意：
// - 这是“最小可用”的接收器：一次只处理一个客户端连接；断开后继续等待下一个连接。
// - TCP 是字节流协议：一次 Read 不一定读满你想要的字节数，所以必须循环读取直到满足长度。
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
        // 解析命令行参数：端口号与输出文件路径。
        // 这里使用 int.Parse：如果传入非法端口会抛异常并终止程序（符合“最小可用”定位）。
        var port = args.Length >= 1 ? int.Parse(args[0]) : 5001;
        var outputPath = args.Length >= 2 ? args[1] : "latest.jpg";

        Console.WriteLine($"[PcReceiver] listen :{port}, output={outputPath}");

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
                RunSession(stream, outputPath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[PcReceiver] session error: {e.Message}");
            }

            Console.WriteLine("[PcReceiver] disconnected.");
        }
    }

    private static void RunSession(NetworkStream stream, string outputPath)
    {
        // 固定头部长度（与发送端一致）：
        // 4(Magic) + 1(Version) + 3(Reserved) + 8(Timestamp) + 4(Width) + 4(Height) + 4(PayloadLength) = 28
        var header = new byte[4 + 1 + 3 + 8 + 4 + 4 + 4];
        // 用于统计 FPS：每满 1 秒输出一次本秒内接收到的帧率与最后一帧的关键信息。
        var sw = Stopwatch.StartNew();
        var frameCount = 0;

        while (true)
        {
            // 从 TCP 流中“精确读取”一个完整 header（TCP 不保证一次 Read 就能拿到完整 header）。
            ReadExactly(stream, header, 0, header.Length);

            // 校验魔数：若不匹配，说明连接数据不是本协议（或被破坏/错位）。
            if (header[0] != Magic[0] || header[1] != Magic[1] || header[2] != Magic[2] || header[3] != Magic[3])
                throw new InvalidDataException("bad magic");

            // 协议版本：便于后续升级兼容（目前仅支持 version=1）。
            var version = header[4];
            if (version != 1)
                throw new InvalidDataException($"unsupported version: {version}");

            // 依照约定的偏移读取字段（全部为小端序）。
            // timestamp 一般来自相机/SDK 的时间戳；width/height 是源帧尺寸；payloadLen 是 JPEG 字节数。
            var timestamp = ReadU64LE(header, 8);
            var width = ReadI32LE(header, 16);
            var height = ReadI32LE(header, 20);
            var payloadLen = ReadI32LE(header, 24);

            // 基本健壮性校验：避免出现负数/超大长度导致内存分配异常或 OOM。
            if (payloadLen <= 0 || payloadLen > 50 * 1024 * 1024)
                throw new InvalidDataException($"bad payloadLen: {payloadLen}");

            // 读取 payload（JPEG 数据本体）。这里按长度一次性分配数组，简单直观。
            var payload = new byte[payloadLen];
            ReadExactly(stream, payload, 0, payloadLen);

            // 输出最新帧文件（最简单的“显示/处理”入口：任何程序都能读 jpg）
            File.WriteAllBytes(outputPath, payload);

            frameCount++;
            if (sw.ElapsedMilliseconds >= 1000)
            {
                // fps = 本段时间内帧数 / 秒数。这里用毫秒避免浮点误差累积。
                var fps = frameCount * 1000.0 / sw.ElapsedMilliseconds;
                Console.WriteLine($"[PcReceiver] fps={fps:F1} last={width}x{height} bytes={payloadLen} ts={timestamp}");
                frameCount = 0;
                sw.Restart();
            }
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
