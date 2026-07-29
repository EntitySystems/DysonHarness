using Microsoft.Extensions.Options;

namespace DysonHarness;

/// <summary>
/// Cloud hosting: read or mint the forever <see cref="DysonSubjectCookie"/>,
/// bind <see cref="DysonScopedSubjectContext"/>, and ensure the subject row when minting.
/// No-op when <see cref="DysonHostingMode.Local"/>.
/// </summary>
public sealed class DysonSubjectCookieMiddleware(
    RequestDelegate next,
    IOptions<DysonHostingOptions> hostingOptions)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IOptions<DysonHostingOptions> _hostingOptions =
        hostingOptions ?? throw new ArgumentNullException(nameof(hostingOptions));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_hostingOptions.Value.Mode != DysonHostingMode.Cloud)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var subjectContext = context.RequestServices.GetRequiredService<DysonScopedSubjectContext>();
        var cookie = context.Request.Cookies[DysonSubjectCookie.Name];
        var minted = false;

        string subjectId;
        if (TryValidateSubjectId(cookie, out var existing))
        {
            subjectId = existing;
        }
        else
        {
            subjectId = Guid.NewGuid().ToString("D");
            minted = true;
        }

        subjectContext.SetSubjectId(subjectId);

        if (minted)
        {
            var settings = context.RequestServices.GetRequiredService<IDysonSubjectSettingsRepository>();
            var ensure = await settings
                .EnsureSubjectAsync(context.RequestAborted)
                .ConfigureAwait(false);
            if (ensure.IsError)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response
                    .WriteAsync(ensure.Error, context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.Cookies.Append(
                DysonSubjectCookie.Name,
                subjectId,
                CreateCookieOptions(context));
        }

        await _next(context).ConfigureAwait(false);
    }

    internal static bool TryValidateSubjectId(string? raw, out string subjectId)
    {
        subjectId = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, DysonSubjects.Shared, StringComparison.Ordinal))
            return false;

        if (!Guid.TryParse(trimmed, out var guid) || guid == Guid.Empty)
            return false;

        subjectId = guid.ToString("D");
        return true;
    }

    internal static CookieOptions CreateCookieOptions(HttpContext context) =>
        new()
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DysonSubjectCookie.ForeverExpiresUtc(),
            IsEssential = true,
            Path = "/",
        };
}
