using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MHG_BoxPrint.Print
{
    public class PrintDataChanged
    {
        public class PrintDataEventArgs : EventArgs
        {
            public string Item { get; set; }
            public string Lot { get; set; }
        }

        public event EventHandler<PrintDataEventArgs> OnPrintDataChanged;

        private string _item;
        private string _lot;

        private bool _itemChanged;
        private bool _lotChanged;

        public void ItemChanged(string item)
        {
            if (_item != item)
            {
                _item = item;
                _itemChanged = true;
                CheckAndRaise();
            }
        }

        public void LotChanged(string lot)
        {
            if (_lot != lot)
            {
                _lot = lot;
                _lotChanged = true;
                CheckAndRaise();
            }
        }

        //private void CheckAndRaise()
        //{
        //    if (_itemChanged && _lotChanged)
        //    {
        //        OnPrintDataChanged?.Invoke(this, new PrintDataEventArgs
        //        {
        //            Item = _item,
        //            Lot = _lot
        //        });

        //        // reset trạng thái
        //        _itemChanged = false;
        //        _lotChanged = false;
        //    }
        //}

        private const int DebounceMs = 10000;

        private readonly object _raiseLock = new object();
        private int _version;
        private bool _firstSent = false;

        private bool HasBothData()
        {
            return !string.IsNullOrEmpty(_item) && !string.IsNullOrEmpty(_lot);
        }

        private void CheckAndRaise()
        {
            if (!(_itemChanged || _lotChanged))
                return;

            // Lần đầu: chưa đủ 2 dữ liệu thì chờ tiếp, chưa gửi
            if (!_firstSent && !HasBothData())
                return;

            // Có thay đổi mới -> lần chờ cũ bị vô hiệu
            int myVersion = System.Threading.Interlocked.Increment(ref _version);

            _ = WaitAndRaiseAsync(myVersion);
        }

        private async System.Threading.Tasks.Task WaitAndRaiseAsync(int myVersion)
        {
            await System.Threading.Tasks.Task.Delay(DebounceMs);

            // Trong 10s có dữ liệu mới hơn -> bỏ qua, lần chờ mới sẽ lo
            if (myVersion != System.Threading.Volatile.Read(ref _version))
                return;

            string item;
            string lot;

            lock (_raiseLock)
            {
                if (!(_itemChanged || _lotChanged))
                    return;

                // Lần đầu vẫn phải đủ 2 dữ liệu
                if (!_firstSent && !HasBothData())
                    return;

                // Giá trị mới nhất tại thời điểm hết 10s
                item = _item;
                lot = _lot;

                _firstSent = true;

                // reset trạng thái
                _itemChanged = false;
                _lotChanged = false;
            }

            OnPrintDataChanged?.Invoke(this, new PrintDataEventArgs
            {
                Item = item,
                Lot = lot
            });
        }

        // Gọi khi đóng/dispose để hủy lần chờ đang treo
        private void StopDebounce()
        {
            System.Threading.Interlocked.Increment(ref _version);
        }
    }
}
