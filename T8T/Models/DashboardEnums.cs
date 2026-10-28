namespace T8T.Models;

/// <summary>Màn hình đang hiển thị trong ứng dụng.</summary>
public enum AppSection
{
    Scada,
    Web,
}

/// <summary>Trạng thái vận hành của một máy trên dây chuyền.</summary>
public enum MachineState
{
    Running,
    Warning,
    Fault,
    Idle,
}

/// <summary>Mức độ nghiêm trọng của cảnh báo.</summary>
public enum AlarmSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>Khoảng thời gian của biểu đồ sản lượng.</summary>
public enum TimeRange
{
    Shift,
    Day,
    Week,
}
