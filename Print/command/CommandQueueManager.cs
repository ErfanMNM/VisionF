using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MHG_BoxPrint.Print.command
{
    public class CommandQueueManager : IDisposable
    {
        private readonly Queue<CommandItem> normalQueue = new Queue<CommandItem>();
        private readonly Queue<CommandItem> midQueue = new Queue<CommandItem>();
        private readonly LinkedList<CommandItem> priorityQueue = new LinkedList<CommandItem>();

        private CancellationTokenSource cts;
        private Task workerTask;

        public EventHandler<string> CommandSent;

        /// <summary>
        /// true nếu worker còn đang chạy (chưa thoát hẳn).
        /// </summary>
        public bool IsRunning
        {
            get
            {
                Task t = workerTask;
                return t != null && !t.IsCompleted;
            }
        }

        public void Start()
        {
            if (workerTask != null)
            {
                // Worker cũ vẫn đang chạy (hoặc Stop trước đó bị timeout) thì không tạo thêm.
                if (!workerTask.IsCompleted)
                    return;

                // Worker cũ đã thoát nhưng chưa được dọn.
                workerTask = null;
                cts?.Dispose();
                cts = null;
            }

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            workerTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    CommandItem item = GetNextItem();

                    try
                    {
                        if (item == null)
                        {
                            await Task.Delay(100, token);
                            continue;
                        }

                        // Đã yêu cầu dừng thì không gửi thêm lệnh nào nữa.
                        if (token.IsCancellationRequested)
                            break;

                        Send(item.Command);

                        await Task.Delay(item.DelayMs, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                    }
                }
            });
        }

        private CommandItem GetNextItem()
        {
            CommandItem item = null;

            lock (priorityQueue)
            {
                if (priorityQueue.Count > 0)
                {
                    item = priorityQueue.First.Value;
                    priorityQueue.RemoveFirst();
                }
            }

            lock (midQueue)
            {
                if (item == null && midQueue.Count > 0)
                {
                    item = midQueue.Dequeue();
                }
            }

            if (item == null)
            {
                lock (normalQueue)
                {
                    if (normalQueue.Count > 0)
                    {
                        item = normalQueue.Dequeue();
                        normalQueue.Enqueue(item); // quay vòng
                    }
                }
            }

            return item;
        }

        /// <summary>
        /// Yêu cầu dừng và chờ worker thoát.
        /// Trả về true nếu worker đã dừng hẳn, false nếu hết timeout mà còn chạy
        /// (ví dụ Send đang bị treo). Khi trả về false có thể gọi Stop lại.
        /// </summary>
        public bool Stop(int timeoutMs = 3000)
        {
            Task task = workerTask;

            if (task == null)
                return true;

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            bool stopped;

            try
            {
                stopped = task.Wait(timeoutMs);
            }
            catch (AggregateException)
            {
                stopped = true; // task đã kết thúc (lỗi/hủy)
            }
            catch
            {
                stopped = task.IsCompleted;
            }

            if (stopped)
            {
                workerTask = null;

                try
                {
                    cts?.Dispose();
                }
                catch
                {
                }

                cts = null;
            }

            return stopped;
        }

        /// <summary>
        /// Thêm lệnh chạy vòng lặp liên tục
        /// </summary>
        public void AddNormal(string command, int delayMs)
        {
            lock (normalQueue)
            {
                normalQueue.Enqueue(new CommandItem(command, delayMs));
            }
        }

        public void AddMid(string command, int delayMs)
        {
            lock (midQueue)
            {
                midQueue.Enqueue(new CommandItem(command, delayMs));
            }
        }

        /// <summary>
        /// Thêm lệnh ưu tiên, chỉ gửi 1 lần
        /// </summary>
        public void AddPriority(string command, int delayMs)
        {
            lock (priorityQueue)
            {
                priorityQueue.AddFirst(new CommandItem(command, delayMs));
            }
        }

        private void Send(string cmd)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} Send => {cmd}");

            // Gọi hàm gửi thực tế của bạn
            // Send(cmd);

            if (CommandSent != null)
            {
                CommandSent.Invoke(this, cmd);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}