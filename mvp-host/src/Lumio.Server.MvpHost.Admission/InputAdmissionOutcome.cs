namespace Lumio.Server.MvpHost.Admission;

public abstract class InputAdmissionOutcome
{
    private InputAdmissionOutcome()
    {
    }

    public sealed class Accepted : InputAdmissionOutcome
    {
        public Accepted(ConnectionBinding binding)
        {
            Binding = binding;
        }

        public ConnectionBinding Binding { get; }
    }

    public sealed class Rejected : InputAdmissionOutcome
    {
        public Rejected(string code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
