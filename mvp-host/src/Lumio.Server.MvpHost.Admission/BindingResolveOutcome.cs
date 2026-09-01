namespace Lumio.Server.MvpHost.Admission;

public abstract class BindingResolveOutcome
{
    private BindingResolveOutcome()
    {
    }

    public sealed class Found : BindingResolveOutcome
    {
        public Found(ConnectionBinding binding)
        {
            Binding = binding;
        }

        public ConnectionBinding Binding { get; }
    }

    public sealed class Rejected : BindingResolveOutcome
    {
        public Rejected(string code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
