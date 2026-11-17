using MHG_BoxPrint.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MHG_BoxPrint.Print
{
    public class PrinterData
    {
        public List<string> DataList { get; set; } = new List<string>();

        public int Index = 0;

        public int SentCount { get; set; } = 0;
        public int SentSuccessCount { get; set; } = 0;
        public int SentFailureCount { get; set; } = 0;
        public int PrintedCount { get; set; } = 0;
        public int PinterDataCount { get; set; } = 0;

        public int MFDLEN { get; set; } = 0;

        public bool ConsecutiveFailure { get; set; } = false;
        private int ConsecutiveFailureCount { get; set; } = 0;

        public override string ToString()
        {
            return $"Đã gửi: {SentCount}/{DataList.Count}, Thành công: {SentSuccessCount}, Lỗi: {SentFailureCount}, Máy in: {MFDLEN}";
        }

        public void AddData(List<string> data, int index)
        {
            DataList = data;
            Index = index;
            Reset();
        }


        public void Reset()
        {
            SentCount = 0;
            SentSuccessCount = 0;
            SentFailureCount = 0;
            PrintedCount = 0;
            PinterDataCount = 0;
            ConsecutiveFailure = false;
            ConsecutiveFailureCount = 0;
            MFDLEN = 0;
        }

        public string Send1Data()
        {
            string data = "";
            if (DataList != null && DataList.Count > SentCount)
            {
                data = DataList[SentCount];
                SentCount++;
                Index++;

                SqlitePrintSendState.UpdateIndex(Global.POLot, Global.POItem, Index);
            }
            return data;
        }

        public void SendSuccess()
        {
            SentSuccessCount++;
            PinterDataCount++;
            ConsecutiveFailureCount = 0;
        }

        public void SendFailure()
        {
            SentFailureCount++;
            ConsecutiveFailureCount++;
            if (ConsecutiveFailureCount>20)
            {
                ConsecutiveFailure = true;
            }
        }

        public void Pinted()
        {
            if (PinterDataCount > 0)
            {
                PinterDataCount--;
            }
            PrintedCount++;
        }
    }
}
