public enum PrinterStatus
{
    /// <summary>
    /// Dùng cái này
    /// </summary>
    Tcp_Connected,
    Tcp_Disconnected,

    /// <summary>
    /// Chỉ xem để biết
    /// </summary>
    Rs232_Connected,
    Rs232_Disconnected,

    Printer_Printing,
    Printer_Stopped,

    /// <summary>
    /// Trả về string Job
    /// </summary>
    Printer_Job,

    AddData_Success,
    AddData_Failure,

    /// <summary>
    /// Trả về string Ink1_Low%
    /// </summary>
    Ink1_Low,

    /// <summary>
    /// Trả về string Ink2_Low%
    /// </summary>
    Ink2_Low,

    Ink1,
    Ink2,
}