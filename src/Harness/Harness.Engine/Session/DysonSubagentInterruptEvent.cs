namespace DysonHarness;

public sealed class DysonSubagentInterruptEvent : DysonAgentSessionEvent
{
    public required DysonAgentInterrupt Interrupt { get; init; }
}
