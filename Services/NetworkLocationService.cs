using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace SpeedEmulator.Services;

public interface INetworkLocationService
{
    Task<NetworkLocation> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed record NetworkLocation(string NetworkIp, string LoginRegion);

public sealed class NetworkLocationService : INetworkLocationService
{
    private const string IpOverrideEnvironmentVariable = "SPEEDEMULATOR_NETWORK_IP_OVERRIDE";
    private const string RegionOverrideEnvironmentVariable = "SPEEDEMULATOR_LOGIN_REGION_OVERRIDE";
    private const string UnknownRegion = "未知地区";

    private static readonly Uri IpIpUri = new("https://myip.ipip.net/json");
    private static readonly Uri IpApiUri = new("http://ip-api.com/json/?lang=zh-CN&fields=status,query,regionName,city");
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public async Task<NetworkLocation> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var overrideIp = Normalize(Environment.GetEnvironmentVariable(IpOverrideEnvironmentVariable));
        var overrideRegion = Normalize(Environment.GetEnvironmentVariable(RegionOverrideEnvironmentVariable));
        if (!string.IsNullOrWhiteSpace(overrideIp) && !string.IsNullOrWhiteSpace(overrideRegion))
        {
            return new NetworkLocation(overrideIp, overrideRegion);
        }

        var externalLocation = await TryGetExternalLocationAsync(cancellationToken);
        var networkIp = string.IsNullOrWhiteSpace(overrideIp)
            ? externalLocation?.NetworkIp ?? GetLocalIpAddress()
            : overrideIp;
        var loginRegion = string.IsNullOrWhiteSpace(overrideRegion)
            ? externalLocation?.LoginRegion ?? UnknownRegion
            : overrideRegion;

        return new NetworkLocation(networkIp, loginRegion);
    }

    private static async Task<NetworkLocation?> TryGetExternalLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            var ipIpLocation = await TryGetIpIpLocationAsync(timeout.Token);
            if (ipIpLocation is not null)
            {
                return ipIpLocation;
            }

            var response = await HttpClient.GetFromJsonAsync<IpApiResponse>(IpApiUri, timeout.Token);
            if (response is null || !string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var ip = Normalize(response.Query);
            if (string.IsNullOrWhiteSpace(ip))
            {
                return null;
            }

            var region = BuildRegion(response.RegionName, response.City);
            return new NetworkLocation(ip, region);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<NetworkLocation?> TryGetIpIpLocationAsync(CancellationToken cancellationToken)
    {
        var response = await HttpClient.GetFromJsonAsync<IpIpResponse>(IpIpUri, cancellationToken);
        if (response is null || !string.Equals(response.Ret, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ip = Normalize(response.Data?.Ip);
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var region = BuildRegionFromLocation(response.Data?.Location);
        return new NetworkLocation(ip, region);
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .FirstOrDefault(address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address))
                ?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static string BuildRegion(string? regionName, string? city)
    {
        var region = Normalize(regionName);
        var cityName = Normalize(city);

        if (string.IsNullOrWhiteSpace(region))
        {
            return string.IsNullOrWhiteSpace(cityName) ? UnknownRegion : cityName;
        }

        if (string.IsNullOrWhiteSpace(cityName) ||
            region.Contains(cityName, StringComparison.OrdinalIgnoreCase) ||
            cityName.Contains(region, StringComparison.OrdinalIgnoreCase))
        {
            return region;
        }

        return $"{region}{cityName}";
    }

    private static string BuildRegionFromLocation(IReadOnlyList<string>? location)
    {
        if (location is null || location.Count < 3)
        {
            return UnknownRegion;
        }

        var province = NormalizeProvince(location[1]);
        var city = NormalizeCity(location[2]);
        if (string.IsNullOrWhiteSpace(province))
        {
            return string.IsNullOrWhiteSpace(city) ? UnknownRegion : city;
        }

        if (string.IsNullOrWhiteSpace(city) ||
            province.Contains(city, StringComparison.OrdinalIgnoreCase) ||
            city.Contains(province, StringComparison.OrdinalIgnoreCase))
        {
            return province;
        }

        return $"{province}{city}";
    }

    private static string NormalizeProvince(string? value)
    {
        var province = Normalize(value);
        if (string.IsNullOrWhiteSpace(province) ||
            province.EndsWith("省", StringComparison.Ordinal) ||
            province.EndsWith("市", StringComparison.Ordinal) ||
            province.EndsWith("自治区", StringComparison.Ordinal) ||
            province.EndsWith("特别行政区", StringComparison.Ordinal))
        {
            return province;
        }

        return province switch
        {
            "北京" or "天津" or "上海" or "重庆" => $"{province}市",
            "广西" or "西藏" or "宁夏" or "新疆" or "内蒙古" => $"{province}自治区",
            "香港" or "澳门" => $"{province}特别行政区",
            _ => $"{province}省"
        };
    }

    private static string NormalizeCity(string? value)
    {
        var city = Normalize(value);
        if (string.IsNullOrWhiteSpace(city) ||
            city.EndsWith("市", StringComparison.Ordinal) ||
            city.EndsWith("地区", StringComparison.Ordinal) ||
            city.EndsWith("盟", StringComparison.Ordinal) ||
            city.EndsWith("自治州", StringComparison.Ordinal) ||
            city.EndsWith("特别行政区", StringComparison.Ordinal))
        {
            return city;
        }

        return $"{city}市";
    }

    private static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private sealed class IpApiResponse
    {
        public string? Status { get; set; }

        public string? Query { get; set; }

        [JsonPropertyName("regionName")]
        public string? RegionName { get; set; }

        public string? City { get; set; }
    }

    private sealed class IpIpResponse
    {
        public string? Ret { get; set; }

        public IpIpData? Data { get; set; }
    }

    private sealed class IpIpData
    {
        public string? Ip { get; set; }

        public List<string>? Location { get; set; }
    }
}
