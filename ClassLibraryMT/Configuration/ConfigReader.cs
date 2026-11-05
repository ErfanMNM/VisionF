using Microsoft.Extensions.Configuration;

namespace ClassLibraryMT.Configuration;

// Doc gia tri cau hinh theo kieu AOT-safe.
//
// Khong dung ConfigurationBinder.GetValue<T>/Bind() vi chung dua tren
// reflection, se sinh canh bao IL2026/IL3050 khi build Native AOT.
// Chi lay string raw roi tu parse lai.
public static class ConfigReader
{
    // Lay chuoi, tra ve fallback neu thieu hoac rong.
    public static string GetString(IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    // Lay so nguyen, tra ve fallback neu thieu hoac khong parse duoc.
    public static int GetInt(IConfiguration config, string key, int fallback)
    {
        var value = config[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    // Lay bool, tra ve fallback neu thieu hoac khong parse duoc.
    //
    // JSON bool ("true"/"false") khi doc qua IConfiguration se ra chuoi
    // "True"/"False", nen phai dung bool.TryParse chu khong dung GetInt.
    public static bool GetBool(IConfiguration config, string key, bool fallback)
    {
        var value = config[key];
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
