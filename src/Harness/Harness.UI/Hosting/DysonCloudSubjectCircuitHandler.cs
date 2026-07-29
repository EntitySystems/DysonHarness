using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Options;

namespace DysonHarness;

/// <summary>
/// Binds the forever subject cookie onto the Blazor circuit's scoped
/// <see cref="DysonScopedSubjectContext"/> (circuit DI is separate from HTTP middleware scope).
/// </summary>
public sealed class DysonCloudSubjectCircuitHandler(
    IHttpContextAccessor httpContextAccessor,
    IOptions<DysonHostingOptions> hostingOptions,
    DysonScopedSubjectContext? subjectContext = null) : CircuitHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    private readonly IOptions<DysonHostingOptions> _hostingOptions =
        hostingOptions ?? throw new ArgumentNullException(nameof(hostingOptions));
    private readonly DysonScopedSubjectContext? _subjectContext = subjectContext;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        if (_hostingOptions.Value.Mode != DysonHostingMode.Cloud || _subjectContext is null)
            return;

        if (_subjectContext.IsSet)
            return;

        var http = _httpContextAccessor.HttpContext;
        if (http is null)
            return;

        var cookie = http.Request.Cookies[DysonSubjectCookie.Name];
        if (!DysonSubjectCookieMiddleware.TryValidateSubjectId(cookie, out var subjectId))
            return;

        _subjectContext.SetSubjectId(subjectId);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
