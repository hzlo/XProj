using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XProj.Plugin.Translator;

public sealed class TranslationService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> TranslateAsync(string text, TranslatorSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return settings.Provider switch
        {
            "Tencent" => await TranslateTencentAsync(text, settings, cancellationToken),
            "Alibaba" => await TranslateAlibabaAsync(text, settings, cancellationToken),
            _ => await TranslateGoogleAsync(text, settings, cancellationToken)
        };
    }

    private static async Task<string> TranslateGoogleAsync(string text, TranslatorSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.GoogleApiKey))
        {
            var request = new Dictionary<string, object?>
            {
                ["q"] = text,
                ["target"] = settings.TargetLanguage,
                ["format"] = "text"
            };
            if (!string.IsNullOrWhiteSpace(settings.SourceLanguage) && settings.SourceLanguage != "auto")
            {
                request["source"] = settings.SourceLanguage;
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(settings.GoogleApiKey)}")
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };
            using var response = await Client.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(DescribeApiError(body, $"Google 翻译服务返回 {(int)response.StatusCode} {response.StatusCode}。"));
            }

            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("data").GetProperty("translations")[0].GetProperty("translatedText").GetString() ?? string.Empty;
        }

        var query = new Dictionary<string, string>
        {
            ["client"] = "gtx",
            ["sl"] = string.IsNullOrWhiteSpace(settings.SourceLanguage) ? "auto" : settings.SourceLanguage,
            ["tl"] = settings.TargetLanguage,
            ["dt"] = "t",
            ["q"] = text
        };
        var url = "https://translate.googleapis.com/translate_a/single?" + await new FormUrlEncodedContent(query).ReadAsStringAsync(cancellationToken);
        using var publicResponse = await Client.GetAsync(url, cancellationToken);
        var publicBody = await publicResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!publicResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeApiError(publicBody, $"Google 翻译服务返回 {(int)publicResponse.StatusCode} {publicResponse.StatusCode}。"));
        }

        using var publicJson = JsonDocument.Parse(publicBody);
        var builder = new StringBuilder();
        foreach (var segment in publicJson.RootElement.EnumerateArray().FirstOrDefault().EnumerateArray())
        {
            if (segment.GetArrayLength() > 0)
            {
                builder.Append(segment[0].GetString());
            }
        }

        return builder.ToString();
    }

    private static async Task<string> TranslateTencentAsync(string text, TranslatorSettings settings, CancellationToken cancellationToken)
    {
        EnsureCredentials(settings.TencentSecretId, settings.TencentSecretKey, "腾讯翻译");
        var secretId = settings.TencentSecretId.Trim();
        var secretKey = settings.TencentSecretKey.Trim();
        var request = new Dictionary<string, object>
        {
            ["SourceText"] = text,
            ["Source"] = NormalizeLanguage(settings.SourceLanguage, allowAuto: true),
            ["Target"] = NormalizeLanguage(settings.TargetLanguage, allowAuto: false),
            ["ProjectId"] = 0
        };
        var result = await SendTencentRequestAsync("TextTranslate", request, secretId, secretKey, settings, cancellationToken);
        using (result)
        {
            return result.RootElement.GetProperty("Response").GetProperty("TargetText").GetString() ?? string.Empty;
        }
    }

    private static async Task<JsonDocument> SendTencentRequestAsync(string action, Dictionary<string, object> request, string secretId, string secretKey, TranslatorSettings settings, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var host = "tmt.tencentcloudapi.com";
        var payload = JsonSerializer.Serialize(request);
        var contentType = "application/json; charset=utf-8";
        var canonicalHeaders = $"content-type:{contentType}\nhost:{host}\n";
        var signedHeaders = "content-type;host";
        var hashedPayload = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{hashedPayload}";
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var service = "tmt";
        var credentialScope = $"{date}/{service}/tc3_request";
        var hashedCanonicalRequest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
        var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{hashedCanonicalRequest}";
        var secretDate = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = Hmac(secretDate, service);
        var secretSigning = Hmac(secretService, "tc3_request");
        var signature = Convert.ToHexString(Hmac(secretSigning, stringToSign)).ToLowerInvariant();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"https://{host}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-TC-Action", action);
        message.Headers.Add("X-TC-Version", "2018-03-21");
        message.Headers.Add("X-TC-Region", settings.TencentRegion);
        message.Headers.Add("X-TC-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Authorization 值中的 SignedHeaders=content-type;host 含分号，会被 HttpHeaders 校验拒绝，必须跳过校验
        message.Headers.TryAddWithoutValidation("Authorization", $"TC3-HMAC-SHA256 Credential={secretId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
        var response = await Client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeApiError(body, $"腾讯翻译服务返回 {(int)response.StatusCode} {response.StatusCode}。"));
        }

        var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("Response", out var responseElement) && responseElement.TryGetProperty("Error", out var errorElement))
        {
            var code = errorElement.TryGetProperty("Code", out var codeValue) ? codeValue.GetString() : null;
            var errorMessage = errorElement.TryGetProperty("Message", out var messageValue) ? messageValue.GetString() : null;
            json.Dispose();
            throw new InvalidOperationException(string.IsNullOrEmpty(code) ? errorMessage ?? "腾讯翻译返回未知错误。" : $"{code}: {errorMessage}");
        }

        return json;
    }

    private static async Task<string> TranslateAlibabaAsync(string text, TranslatorSettings settings, CancellationToken cancellationToken)
    {
        EnsureCredentials(settings.AliAccessKeyId, settings.AliAccessKeySecret, "阿里翻译");
        var host = string.IsNullOrWhiteSpace(settings.AliEndpoint) ? "mt.cn-hangzhou.aliyuncs.com" : new UriBuilder("https://" + settings.AliEndpoint.Trim().TrimEnd('/')).Uri.Host;
        var accessKeyId = settings.AliAccessKeyId.Trim();
        var accessKeySecret = settings.AliAccessKeySecret.Trim();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["FormatType"] = "text",
            ["SourceLanguage"] = NormalizeLanguage(settings.SourceLanguage, allowAuto: true),
            ["TargetLanguage"] = NormalizeLanguage(settings.TargetLanguage, allowAuto: false),
            ["SourceText"] = text
        });

        // 阿里云机器翻译 ROA 风格签名（https://help.aliyun.com/zh/machine-translation/developer-reference/signature-mechanism）
        const string accept = "application/json";
        const string contentType = "application/json; charset=utf-8";
        var date = DateTime.UtcNow.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString();
        var bodyMd5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(payload)));
        var path = "/api/translate/web/general";
        var stringToSign = "POST\n" + accept + "\n" + bodyMd5 + "\n" + contentType + "\n" + date + "\n"
            + "x-acs-signature-method:HMAC-SHA1\n"
            + "x-acs-signature-nonce:" + nonce + "\n"
            + "x-acs-version:2019-01-02\n"
            + path;
        var signature = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(accessKeySecret), Encoding.UTF8.GetBytes(stringToSign)));
        using var message = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}") { Content = new StringContent(payload, Encoding.UTF8) };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json") { CharSet = "utf-8" };
        message.Content.Headers.ContentMD5 = MD5.HashData(Encoding.UTF8.GetBytes(payload));
        message.Headers.TryAddWithoutValidation("Accept", accept);
        message.Headers.TryAddWithoutValidation("Date", date);
        message.Headers.TryAddWithoutValidation("Authorization", $"acs {accessKeyId}:{signature}");
        message.Headers.TryAddWithoutValidation("x-acs-signature-nonce", nonce);
        message.Headers.TryAddWithoutValidation("x-acs-signature-method", "HMAC-SHA1");
        message.Headers.TryAddWithoutValidation("x-acs-version", "2019-01-02");
        var response = await Client.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeApiError(body, $"阿里翻译服务返回 {(int)response.StatusCode} {response.StatusCode}。"));
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.TryGetProperty("Data", out var data) && data.TryGetProperty("Translated", out var translated))
        {
            return translated.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("data", out var lowerData) && lowerData.TryGetProperty("translated", out var lowerTranslated))
        {
            return lowerTranslated.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("阿里翻译返回了无法解析的结果：" + body);
    }

    private static string DescribeApiError(string body, string fallback)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            // 腾讯格式：{"Response":{"Error":{"Code":"...","Message":"..."}}}
            if (root.TryGetProperty("Response", out var response) && response.TryGetProperty("Error", out var error))
            {
                var code = error.TryGetProperty("Code", out var codeValue) ? codeValue.GetString() : null;
                var message = error.TryGetProperty("Message", out var messageValue) ? messageValue.GetString() : null;
                if (!string.IsNullOrEmpty(message))
                {
                    if (code == "AuthFailure.SignatureFailure")
                    {
                        message += "（请确认 SecretKey 与 SecretId 配对、完整无多余空格，然后重新保存设置）";
                    }

                    return string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
                }
            }

            // 阿里 RPC 格式：{"Code":"...","Message":"..."}；ROA 格式：{"RequestId":"...","Message":"..."}
            if (root.TryGetProperty("Message", out var aliMessage))
            {
                var code = root.TryGetProperty("Code", out var aliCode) ? aliCode.GetString() : null;
                return string.IsNullOrEmpty(code) ? aliMessage.GetString() ?? fallback : $"{code}: {aliMessage.GetString()}";
            }

            // Google 格式：{"error":{"message":"..."}}
            if (root.TryGetProperty("error", out var googleError))
            {
                if (googleError.ValueKind == JsonValueKind.String)
                {
                    return googleError.GetString() ?? fallback;
                }

                if (googleError.TryGetProperty("message", out var googleMessage))
                {
                    return googleMessage.GetString() ?? fallback;
                }
            }
        }
        catch (JsonException)
        {
            // 阿里 RPC 网关可能返回 XML 格式错误，如 <Error><Code>..</Code><Message>..</Message></Error>
            if (body.Contains("<Error>", StringComparison.OrdinalIgnoreCase))
            {
                var code = ExtractXmlValue(body, "Code");
                var xmlMessage = ExtractXmlValue(body, "Message");
                if (xmlMessage is not null)
                {
                    return string.IsNullOrEmpty(code) ? xmlMessage : $"{code}: {xmlMessage}";
                }
            }
        }

        return fallback;
    }

    private static string? ExtractXmlValue(string body, string elementName)
    {
        var start = body.IndexOf($"<{elementName}>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += elementName.Length + 2;
        var end = body.IndexOf($"</{elementName}>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : body[start..end];
    }

    private static void EnsureCredentials(string first, string second, string provider)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            throw new InvalidOperationException($"请先在设置中填写{provider}所需的密钥。");
        }
    }

    private static string NormalizeLanguage(string language, bool allowAuto)
    {
        if (allowAuto && string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return language.ToLowerInvariant() switch
        {
            "zh-cn" or "zh-tw" or "zh" => "zh",
            "en-us" or "en" => "en",
            "ja-jp" or "ja" => "ja",
            "ko-kr" or "ko" => "ko",
            "fr-fr" or "fr" => "fr",
            "de-de" or "de" => "de",
            "es-es" or "es" => "es",
            "ru-ru" or "ru" => "ru",
            _ => language
        };
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}
