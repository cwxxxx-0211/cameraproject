using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using OpenCvSharp;

static class Program
{
    private static readonly byte[] Magic = { (byte)'G', (byte)'X', (byte)'R', (byte)'1' };

    // 视频保存配置
    private static readonly string SaveDir = @"D:\savevideo";
    // 设置保存视频的帧率（根据你发送端的实际帧率调整，通常为 30）
    private const double VideoFps = 30.0; 
    
    // 如果你依然想保留模糊剔除，可以解除这段代码的注释
    // private const double LaplacianVarianceThreshold = 100.0;

    static int Main(string[] args)
    {
        var port = args.Length >= 1 ? int.Parse(args[0]) : 5001;
        var windowName = "AR Glasses Real-Time Stream";

        Console.WriteLine($"[PcReceiver] listen :{port}, window=\"{windowName}\"");

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        while (true)
        {
            Console.WriteLine("[PcReceiver] waiting client...");
            using var client = listener.AcceptTcpClient();
            client.NoDelay = true;
            Console.WriteLine($"[PcReceiver] connected: {client.Client.RemoteEndPoint}");

            try
            {
                using var stream = client.GetStream();
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
        var queue = new ConcurrentQueue<byte[]>();
<<<<<<< HEAD
        using var saveQueue = new BlockingCollection<SaveItem>(new ConcurrentQueue<SaveItem>());
=======
        var saveQueue = new ConcurrentQueue<SaveItem>();
>>>>>>> 286f827af0c62ad2275ca4050ad125ac9f31f195
        using var cts = new CancellationTokenSource();

        var header = new byte[28];

        var recvSw = Stopwatch.StartNew();
        var showSw = Stopwatch.StartNew();
        var recvCount = 0;
        var showCount = 0;
        var lastMeta = (timestamp: 0UL, width: 0, height: 0, bytes: 0);

        Directory.CreateDirectory(SaveDir);
        
        // 动态生成视频文件名，例如：record_20240904_143000.mp4
        var videoFilePath = Path.Combine(SaveDir, $"record_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        // 网络接收线程（生产者）
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
                Console.WriteLine($"[PcReceiver] recv thread error: {e.Message}");
                try { cts.Cancel(); } catch { /* ignore */ }
            }
        })
        {
            IsBackground = true,
            Name = "PcReceiver.NetworkReceiver"
        };
        receiverThread.Start();

        // 视频保存线程（消费者 2）
        var saverThread = new Thread(() =>
        {
            VideoWriter videoWriter = null;
            try
            {
<<<<<<< HEAD
                foreach (var item in saveQueue.GetConsumingEnumerable())
                {
=======
                while (!cts.IsCancellationRequested || !saveQueue.IsEmpty)
                {
                    if (!saveQueue.TryDequeue(out var item))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

>>>>>>> 286f827af0c62ad2275ca4050ad125ac9f31f195
                    try
                    {
                        // 在收到第一帧时初始化 VideoWriter，因为我们需要知道准确的宽高
                        if (videoWriter == null)
                        {
                            var size = new Size(item.Mat.Width, item.Mat.Height);
                            // mp4v 编码器广泛支持生成 mp4
                            var fourcc = VideoWriter.FourCC('m', 'p', '4', 'v'); 
                            
<<<<<<< HEAD
                            videoWriter = new VideoWriter(videoFilePath, fourcc, VideoFps, size, isColor: true);
=======
                            videoWriter = new VideoWriter(videoFilePath, fourcc, VideoFps, size);
>>>>>>> 286f827af0c62ad2275ca4050ad125ac9f31f195
                            if (!videoWriter.IsOpened())
                            {
                                Console.WriteLine("[PcReceiver] Failed to open VideoWriter!");
                            }
                            else
                            {
                                Console.WriteLine($"[PcReceiver] Started recording to: {videoFilePath}");
                            }
                        }

                        // 写入当前帧
                        if (videoWriter != null && videoWriter.IsOpened())
                        {
                            videoWriter.Write(item.Mat);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[PcReceiver] save error: {e.Message}");
                    }
                    finally
                    {
                        item.Mat.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[PcReceiver] saver thread error: {e.Message}");
                try { cts.Cancel(); } catch { /* ignore */ }
            }
            finally
            {
                // 必须释放 VideoWriter，否则生成的 MP4 文件尾部损坏，无法播放
                videoWriter?.Release();
                videoWriter?.Dispose();
                Console.WriteLine("[PcReceiver] VideoWriter released. Recording saved.");
            }
        })
        {
<<<<<<< HEAD
            IsBackground = false, // 确保退出前能把 MP4 尾部写完（moov atom）
=======
            IsBackground = true,
>>>>>>> 286f827af0c62ad2275ca4050ad125ac9f31f195
            Name = "PcReceiver.VideoSaver"
        };
        saverThread.Start();

        // 主渲染 UI 线程（消费者 1）
        try
        {
            Cv2.NamedWindow(windowName, WindowFlags.AutoSize);

            while (!cts.IsCancellationRequested)
            {
                if (!queue.TryDequeue(out var payload))
                {
                    Cv2.WaitKey(1);
                    Thread.Sleep(1);
                    continue;
                }

                while (queue.TryDequeue(out var newer))
                {
                    payload = newer;
                }

                using var frame = Cv2.ImDecode(payload, ImreadModes.Color);
                if (!frame.Empty())
                {
                    using var rotated = new Mat();
                    Cv2.Rotate(frame, rotated, RotateFlags.Rotate180);
                    using var corrected = new Mat();
                    Cv2.Flip(rotated, corrected, FlipMode.Y);
                    
                    Cv2.ImShow(windowName, corrected);

                    // 持续将渲染出的最新画面塞入保存队列
<<<<<<< HEAD
                    // 若正在收尾（CompleteAdding）则丢弃，避免异常
                    if (!saveQueue.IsAddingCompleted)
                    {
                        try
                        {
                            saveQueue.Add(new SaveItem(showCount, corrected.Clone()));
                        }
                        catch (InvalidOperationException)
                        {
                            // ignore: adding completed
                        }
                    }
=======
                    saveQueue.Enqueue(new SaveItem(showCount, corrected.Clone()));
>>>>>>> 286f827af0c62ad2275ca4050ad125ac9f31f195
                }

                var key = Cv2.WaitKey(1);
                if (key == 27) // ESC
                {
                    Console.WriteLine("[PcReceiver] ESC pressed, closing session...");
                    break;
                }

                showCount++;

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
            try { cts.Cancel(); } catch { /* ignore */ }
            try { receiverThread.Join(500); } catch { /* ignore */ }
<<<<<<< HEAD

            // 让保存线程“确定性”收尾：停止添加 -> 等待线程写完并释放 VideoWriter
            try { saveQueue.CompleteAdding(); } catch { /* ignore */ }
            try { saverThread.Join(); } catch { /* ignore */ }
=======
            try { saverThread.Join(1000); } catch { /* ignore */ } // 给保存线程多一点时间处理尾盘

            while (saveQueue.TryDequeue(out var mat))
            {
                try { mat.Mat.Dispose(); } catch { /* ignore */ }
            }
>>>>>>> 286f827af0c62ad2275ca4050ad125ac9f31f195

            try { Cv2.DestroyWindow(windowName); } catch { /* ignore */ }
        }
    }

    private readonly struct SaveItem
    {
        public readonly int Index;
        public readonly Mat Mat;

        public SaveItem(int index, Mat mat)
        {
            Index = index;
            Mat = mat;
        }
    }

    private static void ReadExactly(NetworkStream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static int ReadI32LE(byte[] buffer, int offset)
    {
        return buffer[offset + 0]
               | (buffer[offset + 1] << 8)
               | (buffer[offset + 2] << 16)
               | (buffer[offset + 3] << 24);
    }

    private static ulong ReadU64LE(byte[] buffer, int offset)
    {
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