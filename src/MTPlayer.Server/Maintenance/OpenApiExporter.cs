using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace MTPlayer.Server.Maintenance;

public static partial class OpenApiExporter
{
    public static async Task ExportAsync(
        WebApplication app,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("OpenAPI output path cannot be empty.", nameof(outputPath));
        }

        var paths = new JsonObject();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                Path = NormalizePath(endpoint.RoutePattern.RawText),
                Methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [],
            })
            .Where(item => item.Path.StartsWith("/api/", StringComparison.Ordinal) ||
                item.Path.StartsWith("/health/", StringComparison.Ordinal))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

        foreach (var item in endpoints)
        {
            if (paths[item.Path] is not JsonObject pathItem)
            {
                pathItem = new JsonObject();
                paths[item.Path] = pathItem;
            }

            foreach (var method in item.Methods.Order(StringComparer.Ordinal))
            {
                pathItem[method.ToLowerInvariant()] = CreateOperation(item.Endpoint, item.Path, method);
            }
        }

        var document = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = "MT播放器账号与同步 API",
                ["version"] = "v1",
                ["description"] = "账号、设备管理和元数据增量同步接口。服务端不传输媒体内容。",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearerAuth"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["bearerFormat"] = "JWT",
                    },
                },
                ["schemas"] = CreateSchemas(),
            },
        };

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static JsonObject CreateOperation(RouteEndpoint endpoint, string path, string method)
    {
        var operation = new JsonObject
        {
            ["operationId"] = CreateOperationId(method, path),
            ["tags"] = new JsonArray(Tag(path)),
            ["responses"] = Responses(ResponseSchema(method, path)),
        };
        var parameters = Parameters(path);
        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        var requestSchema = RequestSchema(method, path);
        if (requestSchema is not null)
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = JsonContent(Reference(requestSchema)),
            };
        }

        var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
        if (authorized && !anonymous)
        {
            operation["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() });
        }

        return operation;
    }

    private static JsonArray Parameters(string path)
    {
        var result = new JsonArray();
        foreach (Match match in PathParameter().Matches(path))
        {
            var name = match.Groups[1].Value;
            result.Add(new JsonObject
            {
                ["name"] = name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                    ? StringSchema("uuid")
                    : StringSchema(),
            });
        }

        if (path == "/api/v1/sync/pull")
        {
            result.Add(QueryParameter("cursor", "integer", "int64", true));
            result.Add(QueryParameter("limit", "integer", "int32", true));
        }
        else if (path == "/api/v1/auth/tv/device-code")
        {
            result.Add(QueryParameter("serverName", "string", null, false));
        }

        return result;
    }

    private static JsonObject QueryParameter(string name, string type, string? format, bool required)
    {
        var schema = new JsonObject { ["type"] = type };
        if (format is not null)
        {
            schema["format"] = format;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["in"] = "query",
            ["required"] = required,
            ["schema"] = schema,
        };
    }

    private static string? RequestSchema(string method, string path)
    {
        if (method != "POST" && method != "PUT")
        {
            return null;
        }

        return path switch
        {
            "/api/v1/auth/register" => "RegisterRequest",
            "/api/v1/auth/login" => "LoginRequest",
            "/api/v1/auth/refresh" => "RefreshRequest",
            "/api/v1/auth/verify-email" => "VerifyEmailRequest",
            "/api/v1/auth/forgot-password" => "ForgotPasswordRequest",
            "/api/v1/auth/reset-password" => "ResetPasswordRequest",
            "/api/v1/auth/tv/token" => "TvTokenRequest",
            "/api/v1/auth/tv/approve" => "TvApprovalRequest",
            "/api/v1/sync/push" => "SyncPushRequest",
            "/api/v1/admin/settings" => "AdminSettingsUpdate",
            "/api/v1/admin/email/test" => "TestEmailRequest",
            _ => null,
        };
    }

    private static string? ResponseSchema(string method, string path) => (method, path) switch
    {
        ("POST", "/api/v1/auth/login") => "TokenResponse",
        ("POST", "/api/v1/auth/refresh") => "TokenResponse",
        ("POST", "/api/v1/auth/tv/token") => "TokenResponse",
        ("GET", "/api/v1/auth/tv/device-code") => "DeviceCodeResponse",
        ("POST", "/api/v1/sync/push") => "SyncPushResultList",
        ("GET", "/api/v1/sync/pull") => "SyncPullResponse",
        _ => null,
    };

    private static JsonObject Responses(string? schema)
    {
        var success = new JsonObject { ["description"] = "Success" };
        if (schema is not null)
        {
            success["content"] = JsonContent(Reference(schema));
        }

        return new JsonObject
        {
            ["200"] = success,
            ["400"] = new JsonObject { ["description"] = "Invalid request" },
            ["401"] = new JsonObject { ["description"] = "Authentication required" },
            ["403"] = new JsonObject { ["description"] = "Forbidden" },
            ["429"] = new JsonObject { ["description"] = "Rate limited" },
        };
    }

    private static JsonObject JsonContent(JsonObject schema) => new()
    {
        ["application/json"] = new JsonObject { ["schema"] = schema },
    };

    private static JsonObject Reference(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };

    private static JsonObject CreateSchemas()
    {
        var syncKind = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("ConfigurationGroup", "Favorite", "PlaybackHistory", "SkipMarker", "Preference"),
        };
        var syncMutation = ObjectSchema(new Dictionary<string, JsonNode?>
        {
            ["id"] = StringSchema("uuid"),
            ["kind"] = Reference("SyncEntityKind"),
            ["baseVersion"] = IntegerSchema("int64", 0),
            ["modifiedAtUtc"] = StringSchema("date-time"),
            ["isDeleted"] = new JsonObject { ["type"] = "boolean" },
            ["payload"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
        });

        return new JsonObject
        {
            ["SyncEntityKind"] = syncKind,
            ["RegisterRequest"] = ObjectSchema(new() { ["email"] = StringSchema("email"), ["password"] = StringSchema() }),
            ["LoginRequest"] = ObjectSchema(new()
            {
                ["email"] = StringSchema("email"), ["password"] = StringSchema(),
                ["deviceName"] = StringSchema(), ["platform"] = StringSchema(),
            }),
            ["RefreshRequest"] = ObjectSchema(new() { ["refreshToken"] = StringSchema() }),
            ["VerifyEmailRequest"] = ObjectSchema(new() { ["token"] = StringSchema() }),
            ["ForgotPasswordRequest"] = ObjectSchema(new() { ["email"] = StringSchema("email") }),
            ["ResetPasswordRequest"] = ObjectSchema(new() { ["token"] = StringSchema(), ["password"] = StringSchema() }),
            ["TvTokenRequest"] = ObjectSchema(new() { ["deviceCode"] = StringSchema() }),
            ["TvApprovalRequest"] = ObjectSchema(new() { ["userCode"] = StringSchema() }),
            ["TokenResponse"] = ObjectSchema(new()
            {
                ["accessToken"] = StringSchema(), ["refreshToken"] = StringSchema(),
                ["expiresAtUtc"] = StringSchema("date-time"),
                ["emailVerified"] = new JsonObject { ["type"] = "boolean" },
            }),
            ["DeviceCodeResponse"] = ObjectSchema(new()
            {
                ["deviceCode"] = StringSchema(), ["userCode"] = StringSchema(),
                ["verificationUri"] = StringSchema("uri"), ["expiresAtUtc"] = StringSchema("date-time"),
                ["pollIntervalSeconds"] = IntegerSchema("int32", 1),
            }),
            ["SyncMutation"] = syncMutation,
            ["SyncPushRequest"] = ObjectSchema(new()
            {
                ["deviceId"] = StringSchema("uuid"),
                ["mutations"] = ArraySchema(Reference("SyncMutation"), 500),
            }),
            ["SyncPushResult"] = ObjectSchema(new()
            {
                ["id"] = StringSchema("uuid"), ["version"] = IntegerSchema("int64", 0),
                ["serverModifiedAtUtc"] = StringSchema("date-time"), ["accepted"] = new JsonObject { ["type"] = "boolean" },
                ["errorCode"] = NullableString(), ["current"] = Reference("SyncMutation"),
            }),
            ["SyncPushResultList"] = ArraySchema(Reference("SyncPushResult"), 500),
            ["SyncPullResponse"] = ObjectSchema(new()
            {
                ["cursor"] = IntegerSchema("int64", 0),
                ["changes"] = ArraySchema(Reference("SyncMutation"), 500),
            }),
            ["AdminSettingsUpdate"] = LooseObjectSchema(new()
            {
                ["publicBaseUrl"] = NullableString(), ["smtpHost"] = NullableString(),
                ["smtpPort"] = IntegerSchema("int32", 1), ["smtpUsername"] = NullableString(),
                ["newSmtpPassword"] = new JsonObject { ["type"] = "string", ["nullable"] = true, ["writeOnly"] = true },
                ["smtpFromName"] = NullableString(), ["smtpFromAddress"] = NullableString(),
                ["smtpUseTls"] = new JsonObject { ["type"] = "boolean" },
                ["registrationEnabled"] = new JsonObject { ["type"] = "boolean" },
                ["requireVerifiedEmail"] = new JsonObject { ["type"] = "boolean" },
                ["passwordResetEnabled"] = new JsonObject { ["type"] = "boolean" },
                ["emailVerificationTokenExpiryMinutes"] = IntegerSchema("int32", 1),
                ["passwordResetTokenExpiryMinutes"] = IntegerSchema("int32", 1),
                ["verificationSubjectTemplate"] = NullableString(), ["verificationBodyTemplate"] = NullableString(),
                ["resetSubjectTemplate"] = NullableString(), ["resetBodyTemplate"] = NullableString(),
                ["testSubjectTemplate"] = NullableString(), ["testBodyTemplate"] = NullableString(),
                ["clearPublicBaseUrl"] = new JsonObject { ["type"] = "boolean" },
                ["clearSmtpPassword"] = new JsonObject { ["type"] = "boolean" },
            }),
            ["TestEmailRequest"] = ObjectSchema(new() { ["recipientEmail"] = StringSchema("email") }),
        };
    }

    private static JsonObject ObjectSchema(Dictionary<string, JsonNode?> properties) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray(properties.Keys
            .Select(key => (JsonNode?)JsonValue.Create(key))
            .ToArray()),
        ["properties"] = new JsonObject(properties),
        ["additionalProperties"] = false,
    };

    private static JsonObject LooseObjectSchema(Dictionary<string, JsonNode?> properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties),
        ["additionalProperties"] = false,
    };

    private static JsonObject StringSchema(string? format = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (format is not null)
        {
            schema["format"] = format;
        }

        return schema;
    }

    private static JsonObject NullableString() => new() { ["type"] = "string", ["nullable"] = true };

    private static JsonObject IntegerSchema(string format, long minimum) => new()
    {
        ["type"] = "integer",
        ["format"] = format,
        ["minimum"] = minimum,
    };

    private static JsonObject ArraySchema(JsonObject items, int maximum) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maximum,
        ["items"] = items,
    };

    private static string NormalizePath(string? rawPath)
    {
        var path = PathConstraint().Replace(rawPath ?? string.Empty, "{$1}");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string Tag(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 ? segments[2] : "health";
    }

    private static string CreateOperationId(string method, string path) =>
        $"{method.ToLowerInvariant()}_{NonAlphaNumeric().Replace(path.Trim('/'), "_").Trim('_')}";

    [GeneratedRegex("\\{([^}:]+):[^}]+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PathConstraint();

    [GeneratedRegex("\\{([^}]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PathParameter();

    [GeneratedRegex("[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();
}
