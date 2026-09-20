using System.Text.Json;
using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>
/// Remotion promo storyline, played through the live engine + UI.
/// Prompt, plan steps, seven subagents, ClientBillService, cascading tool statuses.
/// </summary>
public static class DysonVisualDemoScenario
{
    public const string SessionTitle = "DEMO: Move DB calls to repositories";
    public const string WorkDirectoryName = "Billing (DEMO)";
    public const string DemoSlug = "demo-mock";
    public const int ExpectedSubagentCount = 7;

    public const string UserPrompt = "make a plan to move database calls to repositories";

    public const string InventoryClientTask = "Inventory ClientBillService database calls";
    public const string InventoryRestTask = "Inventory remaining billing service DB calls";
    public const string InterfacesTask = "Add IClientBillRepository and EF adapter";
    public const string MigrateClientTask = "Migrate ClientBillService onto the repository";
    public const string MigrateRestTask = "Migrate remaining billing services";
    public const string TestsTask = "Add repository and service tests";
    public const string VerifyTask = "Verify no leftover database calls";

    public static IReadOnlyList<DysonToolCall> SeedTools(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        if (session.Parent is not null
            || string.Equals(session.Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase)
            || string.Equals(session.Mode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
        {
            return SeedChild(session, turn);
        }

        if (turn.Kind == DysonAgentTurnKind.SubagentReportProcessing)
            return SeedRootHandoff(session);

        if (!HasUserKickoff(session))
            return SeedRootKickoff();

        return [];
    }

    public static string ComposeThought(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        var hint = ChildHint(session, turn);

        if (hint.Contains("Inventory ClientBill", StringComparison.OrdinalIgnoreCase))
            return "# Inventory ClientBillService\n\nRead the service, then grep every DbContext call.";
        if (hint.Contains("Inventory remaining", StringComparison.OrdinalIgnoreCase))
            return "# Inventory remaining services\n\nInvoiceService and the shared BillingDbContext next.";
        if (hint.Contains("IClientBillRepository", StringComparison.OrdinalIgnoreCase))
            return "# Interfaces\n\nExtract IClientBillRepository before touching call sites.";
        if (hint.Contains("Migrate ClientBill", StringComparison.OrdinalIgnoreCase))
            return "# Migrate ClientBillService\n\nReplace BillingDbContext usage with the repository.";
        if (hint.Contains("Migrate remaining", StringComparison.OrdinalIgnoreCase))
            return "# Migrate remaining services\n\nSame pattern on InvoiceService.";
        if (hint.Contains("service tests", StringComparison.OrdinalIgnoreCase))
            return "# Tests\n\nCover the repository adapter and the migrated service.";
        if (hint.Contains("Verify no leftover", StringComparison.OrdinalIgnoreCase))
            return "# Verify\n\nGrep, build, and spot-check the billing UI for leftover DB calls.";

        if (turn.Kind == DysonAgentTurnKind.SubagentReportProcessing)
            return "# Subagent handoff\n\nRead the report, update the plan, dispatch the next wave.";

        return "# Plan\n\nInventory → interfaces → migrate → tests → verify. Explore first, then Drones.";
    }

    public static string ComposeReply(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        var hint = ChildHint(session, turn);

        if (hint.Contains("Inventory ClientBill", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Inventory: ClientBillService

                `ClientBillService` talks to `BillingDbContext` for client bills, balances, and
                invoice lines. Call sites to move behind `IClientBillRepository`:
                `GetOpenBills`, `RecordPayment`, `GetClientBalance`.

                Report submitted to parent.
                """;
        }

        if (hint.Contains("Inventory remaining", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Inventory: remaining services

                `InvoiceService` still new's `BillingDbContext`. Shared helpers in
                `BillingDbContext` stay — only service call sites migrate.

                Report submitted to parent.
                """;
        }

        if (hint.Contains("IClientBillRepository", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Interfaces ready

                Added `IClientBillRepository` and an EF adapter. `ClientBillService` can take
                the interface next without changing public methods.

                Report submitted to parent.
                """;
        }

        if (hint.Contains("Migrate ClientBill", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # ClientBillService migrated

                Constructor now takes `IClientBillRepository`. DbContext calls in
                `GetOpenBills` / `RecordPayment` / `GetClientBalance` are gone.

                Report submitted to parent.
                """;
        }

        if (hint.Contains("Migrate remaining", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Remaining services migrated

                `InvoiceService` uses the repository. One browser smoke selector missed —
                verify will catch leftover UI bindings.

                Report submitted to parent.
                """;
        }

        if (hint.Contains("service tests", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Tests

                Repository adapter + `ClientBillService` payment/balance cases are green.

                Report submitted to parent.
                """;
        }

        if (hint.Contains("Verify no leftover", StringComparison.OrdinalIgnoreCase))
        {
            return """
                # Verify

                No `BillingDbContext` left in services. Build is clean. Billing UI no longer
                reads the context from the page.

                Report submitted to parent.
                """;
        }

        if (turn.Kind == DysonAgentTurnKind.SubagentReportProcessing)
        {
            if (HasChild(session, "Verify no leftover") && AllMatchingTerminal(session, "Verify no leftover"))
            {
                return """
                    # Handoff: repository migration complete

                    | Turn | Subagent | Status |
                    | --- | --- | --- |
                    | 1 | Inventory ClientBillService | completed |
                    | 2 | Inventory remaining services | completed |
                    | 3 | IClientBillRepository + adapter | completed |
                    | 4 | Migrate ClientBillService | completed |
                    | 5 | Migrate remaining services | completed |
                    | 6 | Tests | completed |
                    | 7 | Verify | completed |

                    `ClientBillService` is on `IClientBillRepository`. Plan steps
                    inventory → interfaces → migrate → tests → verify are done.
                    """;
            }

            return """
                # Subagent report

                Report received. Updating the plan and starting the next wave.
                """;
        }

        return """
            # Plan: move database calls to repositories

            Turn-based Work session — not chat. Five steps, seven subagents:

            1. **Inventory** — ClientBillService, then remaining billing services
            2. **Interfaces** — `IClientBillRepository` + EF adapter
            3. **Migrate** — ClientBillService first, then the rest
            4. **Tests** — repository + service
            5. **Verify** — no leftover DbContext calls

            Two Explore inventories are in flight. Each handoff is its own turn.
            """;
    }

    public static bool IsFailedDemoTool(DysonToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return string.Equals(call.ToolName, "BrowserWaitForSelector", StringComparison.OrdinalIgnoreCase);
    }

    public static string MockToolContent(DysonToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var name = call.ToolName;
        var path = ReadPath(call.ArgumentsJson);

        if (string.Equals(name, "Grep", StringComparison.OrdinalIgnoreCase))
        {
            return """
                src/Billing/ClientBillService.cs:18:        _db = new BillingDbContext(options);
                src/Billing/ClientBillService.cs:27:        return _db.ClientBills.Where(b => b.ClientId == clientId && !b.IsClosed).ToList();
                src/Billing/ClientBillService.cs:41:        _db.Payments.Add(payment);
                src/Billing/InvoiceService.cs:14:        using var db = new BillingDbContext(options);
                src/Billing/BillingDbContext.cs:8:public sealed class BillingDbContext : DbContext
                """;
        }

        if (string.Equals(name, "ReadFile", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Contains("IClientBillRepository", StringComparison.OrdinalIgnoreCase))
                return IClientBillRepositoryListing;
            if (path.Contains("InvoiceService", StringComparison.OrdinalIgnoreCase))
                return InvoiceServiceListing;
            return ClientBillServiceListing;
        }

        if (string.Equals(name, "ListDirectory", StringComparison.OrdinalIgnoreCase))
        {
            return """
                src/Billing/BillingDbContext.cs
                src/Billing/ClientBillService.cs
                src/Billing/InvoiceService.cs
                """;
        }

        if (string.Equals(name, "WriteFile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "CreateFile", StringComparison.OrdinalIgnoreCase))
        {
            return $$"""{"ok":true,"path":"{{path}}","edits":1}""";
        }

        if (string.Equals(name, "ShellExecute", StringComparison.OrdinalIgnoreCase))
        {
            return """
                exitCode=0
                Build succeeded.
                    0 Warning(s)
                    0 Error(s)
                """;
        }

        if (string.Equals(name, "BrowserWaitForSelector", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout: #legacy-db-banner was not found (good — page no longer binds BillingDbContext).";
        }

        if (name.StartsWith("Browser", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "OpenBrowser", StringComparison.OrdinalIgnoreCase))
        {
            return """{"ok":true,"windowId":1,"tabId":1,"url":"http://localhost:5180/billing"}""";
        }

        return $"[demo] {name} ok — args={Truncate(call.ArgumentsJson, 80)}";
    }

    public static string EnsurePromoWorkspace()
    {
        var dest = Environment.GetEnvironmentVariable("DYSON_VISUAL_DEMO_WORKDIR");
        if (string.IsNullOrWhiteSpace(dest))
            dest = Path.Combine(DysonAppPaths.GetRoot(DysonBuildInfo.Current), "visual-demo-workspace");

        Directory.CreateDirectory(Path.Combine(dest, "src", "Billing"));
        WriteIfMissing(Path.Combine(dest, "README.md"), PromoReadme);
        WriteIfMissing(Path.Combine(dest, "src", "Billing", "BillingDbContext.cs"), BillingDbContextSource);
        WriteIfMissing(Path.Combine(dest, "src", "Billing", "ClientBillService.cs"), ClientBillServiceSource);
        WriteIfMissing(Path.Combine(dest, "src", "Billing", "InvoiceService.cs"), InvoiceServiceSource);
        return dest;
    }

    public static bool HasUserKickoff(DysonAgentSession session) =>
        session.Turns.Any(t =>
            t.Kind is DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession);

    private static IReadOnlyList<DysonToolCall> SeedRootKickoff() =>
    [
        Call("RenameSession", 0, Json(new { title = SessionTitle })),
        Call("Grep", 0, Json(new { pattern = "BillingDbContext|new BillingDbContext", path = "src/Billing", glob = "*.cs" })),
        Call("ReadFile", 0, Json(new { path = "src/Billing/ClientBillService.cs" })),
        Call("ListDirectory", 1, Json(new { path = "src/Billing" })),
        Call("CreateTodo", 1, Json(new { taskCode = "inventory", displayName = "Inventory database call sites", status = "ongoing" })),
        Call("CreateTodo", 1, Json(new { taskCode = "interfaces", displayName = "Add repository interfaces", status = "pending" })),
        Call("CreateTodo", 1, Json(new { taskCode = "migrate", displayName = "Migrate services onto repositories", status = "pending" })),
        Call("CreateTodo", 1, Json(new { taskCode = "tests", displayName = "Add repository and service tests", status = "pending" })),
        Call("CreateTodo", 1, Json(new { taskCode = "verify", displayName = "Verify no leftover database calls", status = "pending" })),
        Call("StartSubagent", 2, Json(new { agentMode = DysonAgentModes.Explore, task = InventoryClientTask })),
        Call("StartSubagent", 3, Json(new { agentMode = DysonAgentModes.Explore, task = InventoryRestTask })),
    ];

    private static IReadOnlyList<DysonToolCall> SeedRootHandoff(DysonAgentSession session)
    {
        var tools = new List<DysonToolCall>();

        if (AllMatchingTerminal(session, "Inventory") && !HasChild(session, "IClientBillRepository"))
        {
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "inventory", status = "complete", appendComment = "Both inventory reports in." })));
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "interfaces", status = "ongoing" })));
            tools.Add(Call("StartSubagent", 1, Json(new { agentMode = DysonAgentModes.Drone, task = InterfacesTask })));
            return tools;
        }

        if (AllMatchingTerminal(session, "IClientBillRepository") && !HasChild(session, "Migrate ClientBillService"))
        {
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "interfaces", status = "complete" })));
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "migrate", status = "ongoing" })));
            tools.Add(Call("StartSubagent", 1, Json(new { agentMode = DysonAgentModes.Drone, task = MigrateClientTask })));
            tools.Add(Call("StartSubagent", 2, Json(new { agentMode = DysonAgentModes.Drone, task = MigrateRestTask })));
            return tools;
        }

        if (AllMatchingTerminal(session, "Migrate") && !HasChild(session, "service tests"))
        {
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "migrate", status = "complete", appendComment = "ClientBillService + remaining services." })));
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "tests", status = "ongoing" })));
            tools.Add(Call("StartSubagent", 1, Json(new { agentMode = DysonAgentModes.Drone, task = TestsTask })));
            return tools;
        }

        if (AllMatchingTerminal(session, "service tests") && !HasChild(session, "Verify no leftover"))
        {
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "tests", status = "complete" })));
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "verify", status = "ongoing" })));
            tools.Add(Call("StartSubagent", 1, Json(new { agentMode = DysonAgentModes.Explore, task = VerifyTask })));
            return tools;
        }

        if (AllMatchingTerminal(session, "Verify no leftover"))
        {
            tools.Add(Call("UpdateTodo", 0, Json(new { taskCode = "verify", status = "complete" })));
            tools.Add(Call("ListTodos", 1, "{}"));
            tools.Add(Call("ListSubagents", 1, "{}"));
        }

        return tools;
    }

    private static IReadOnlyList<DysonToolCall> SeedChild(DysonAgentSession session, DysonAgentTurn turn)
    {
        var hint = ChildHint(session, turn);

        if (hint.Contains("Inventory ClientBill", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("Grep", 0, Json(new { pattern = "BillingDbContext|_db\\.", path = "src/Billing/ClientBillService.cs" })),
                Call("ReadFile", 0, Json(new { path = "src/Billing/ClientBillService.cs" })),
                Call("ListDirectory", 1, Json(new { path = "src/Billing" })),
                Call("SubmitSubagentReport", 2, Json(new
                {
                    status = "completed",
                    summary = "ClientBillService constructs BillingDbContext and uses it in GetOpenBills, RecordPayment, GetClientBalance.",
                })),
            ];
        }

        if (hint.Contains("Inventory remaining", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("Grep", 0, Json(new { pattern = "new BillingDbContext", path = "src/Billing", glob = "*.cs" })),
                Call("ReadFile", 0, Json(new { path = "src/Billing/InvoiceService.cs" })),
                Call("SubmitSubagentReport", 2, Json(new
                {
                    status = "completed",
                    summary = "InvoiceService also news BillingDbContext. Shared context class should stay.",
                })),
            ];
        }

        if (hint.Contains("IClientBillRepository", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("ReadFile", 0, Json(new { path = "src/Billing/ClientBillService.cs" })),
                Call("CreateFile", 1, Json(new { path = "src/Billing/IClientBillRepository.cs", content = IClientBillRepositorySource })),
                Call("CreateFile", 1, Json(new { path = "src/Billing/ClientBillRepository.cs", content = "// EF adapter implementing IClientBillRepository" })),
                Call("SubmitSubagentReport", 2, Json(new
                {
                    status = "completed",
                    summary = "IClientBillRepository and EF adapter added. Ready to migrate ClientBillService.",
                })),
            ];
        }

        if (hint.Contains("Migrate ClientBill", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("ReadFile", 0, Json(new { path = "src/Billing/ClientBillService.cs" })),
                Call("ReadFile", 0, Json(new { path = "src/Billing/IClientBillRepository.cs" })),
                Call("WriteFile", 1, """{"path":"src/Billing/ClientBillService.cs","old_text":"        _db = new BillingDbContext(options);","new_text":"        _bills = bills;"}"""),
                Call("ShellExecute", 2, Json(new { shell = "pwsh", command = "dotnet build src/Billing", timeoutMs = 30000 })),
                Call("SubmitSubagentReport", 3, Json(new
                {
                    status = "completed",
                    summary = "ClientBillService now depends on IClientBillRepository. Build succeeded.",
                })),
            ];
        }

        if (hint.Contains("Migrate remaining", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("Grep", 0, Json(new { pattern = "new BillingDbContext", path = "src/Billing/InvoiceService.cs" })),
                Call("WriteFile", 1, """{"path":"src/Billing/InvoiceService.cs","old_text":"using var db = new BillingDbContext(options);","new_text":"var invoices = _invoices;"}"""),
                Call("OpenBrowser", 2, Json(new { url = "http://localhost:5180/billing", timeoutMs = 5000 })),
                Call("BrowserWaitForSelector", 3, Json(new { selector = "#legacy-db-banner", timeoutMs = 2000 })),
                Call("SubmitSubagentReport", 4, Json(new
                {
                    status = "completed",
                    summary = "InvoiceService migrated. Browser smoke missed #legacy-db-banner (expected after the cut-over).",
                })),
            ];
        }

        if (hint.Contains("service tests", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                Call("CreateFile", 0, Json(new { path = "src/Billing.Tests/ClientBillServiceTests.cs", content = "// payment + balance cases against a fake IClientBillRepository" })),
                Call("ShellExecute", 1, Json(new { shell = "pwsh", command = "dotnet test src/Billing.Tests", timeoutMs = 30000 })),
                Call("SubmitSubagentReport", 2, Json(new
                {
                    status = "completed",
                    summary = "Repository and ClientBillService tests passed.",
                })),
            ];
        }

        return
        [
            Call("Grep", 0, Json(new { pattern = "new BillingDbContext", path = "src", glob = "*.cs" })),
            Call("ShellExecute", 1, Json(new { shell = "pwsh", command = "dotnet build", timeoutMs = 30000 })),
            Call("OpenBrowser", 2, Json(new { url = "http://localhost:5180/billing", timeoutMs = 5000 })),
            Call("BrowserNavigate", 3, Json(new { url = "http://localhost:5180/billing/clients/42", timeoutMs = 5000 })),
            Call("SubmitSubagentReport", 4, Json(new
            {
                status = "completed",
                summary = "No leftover BillingDbContext in services. Verify build and billing page are clean.",
            })),
        ];
    }

    private static string ChildHint(DysonAgentSession session, DysonAgentTurn turn) =>
        $"{session.DisplayTitle} {turn.Instruction}";

    private static bool HasChild(DysonAgentSession session, string titleNeedle) =>
        session.SubSessions.Any(child =>
            (child.DisplayTitle ?? "").Contains(titleNeedle, StringComparison.OrdinalIgnoreCase));

    private static bool AllMatchingTerminal(DysonAgentSession session, string titleNeedle)
    {
        var matches = session.SubSessions
            .Where(child => (child.DisplayTitle ?? "").Contains(titleNeedle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count > 0 && matches.All(child => child.IsTerminal);
    }

    private static DysonToolCall Call(string toolName, int stage, string argumentsJson) =>
        new()
        {
            CallId = "",
            ToolName = toolName,
            Stage = stage,
            ArgumentsJson = argumentsJson,
        };

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string ReadPath(string? argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                return path.GetString() ?? "";
        }
        catch (JsonException)
        {
        }

        return "";
    }

    private static void WriteIfMissing(string path, string contents)
    {
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, contents);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? "";
        return value[..max] + "…";
    }

    private const string PromoReadme = """
        Billing (DEMO)
        Scripted Demo Mode workspace for the repository-migration promo.
        """;

    private const string BillingDbContextSource = """
        using Microsoft.EntityFrameworkCore;

        namespace Billing;

        public sealed class BillingDbContext : DbContext
        {
            public DbSet<ClientBill> ClientBills => Set<ClientBill>();
            public DbSet<Payment> Payments => Set<Payment>();
        }
        """;

    private const string ClientBillServiceSource = """
        namespace Billing;

        public sealed class ClientBillService
        {
            private readonly BillingDbContext _db;

            public ClientBillService(DbContextOptions options)
            {
                _db = new BillingDbContext(options);
            }

            public IReadOnlyList<ClientBill> GetOpenBills(int clientId) =>
                _db.ClientBills.Where(b => b.ClientId == clientId && !b.IsClosed).ToList();

            public void RecordPayment(int billId, decimal amount)
            {
                var payment = new Payment { BillId = billId, Amount = amount };
                _db.Payments.Add(payment);
                _db.SaveChanges();
            }

            public decimal GetClientBalance(int clientId) =>
                _db.ClientBills.Where(b => b.ClientId == clientId).Sum(b => b.Balance);
        }
        """;

    private const string InvoiceServiceSource = """
        namespace Billing;

        public sealed class InvoiceService
        {
            public Invoice Load(int invoiceId, DbContextOptions options)
            {
                using var db = new BillingDbContext(options);
                return db.Set<Invoice>().First(i => i.Id == invoiceId);
            }
        }
        """;

    private const string IClientBillRepositorySource = """
        namespace Billing;

        public interface IClientBillRepository
        {
            IReadOnlyList<ClientBill> GetOpenBills(int clientId);
            void RecordPayment(int billId, decimal amount);
            decimal GetClientBalance(int clientId);
        }
        """;

    private const string ClientBillServiceListing = """
        1|public sealed class ClientBillService
        2|{
        3|    private readonly BillingDbContext _db;
        4|
        5|    public ClientBillService(DbContextOptions options)
        6|    {
        7|        _db = new BillingDbContext(options);
        8|    }
        9|
        10|    public IReadOnlyList<ClientBill> GetOpenBills(int clientId) =>
        11|        _db.ClientBills.Where(b => b.ClientId == clientId && !b.IsClosed).ToList();
        """;

    private const string InvoiceServiceListing = """
        1|public sealed class InvoiceService
        2|{
        3|    public Invoice Load(int invoiceId, DbContextOptions options)
        4|    {
        5|        using var db = new BillingDbContext(options);
        6|        return db.Set<Invoice>().First(i => i.Id == invoiceId);
        7|    }
        8|}
        """;

    private const string IClientBillRepositoryListing = """
        1|public interface IClientBillRepository
        2|{
        3|    IReadOnlyList<ClientBill> GetOpenBills(int clientId);
        4|    void RecordPayment(int billId, decimal amount);
        5|    decimal GetClientBalance(int clientId);
        6|}
        """;
}
