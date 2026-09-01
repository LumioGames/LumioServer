namespace Lumio.Server.MvpHost.Admission;

public abstract class RoomAdmitOutcome
{
    private RoomAdmitOutcome()
    {
    }

    public sealed class Accepted : RoomAdmitOutcome
    {
        public Accepted(
            ConnectionBinding binding,
            TakeoverNotice? terminationNotice,
            string? supersededConnectionId)
        {
            Binding = binding;
            TerminationNotice = terminationNotice;
            SupersededConnectionId = supersededConnectionId;
        }

        public ConnectionBinding Binding { get; }

        public TakeoverNotice? TerminationNotice { get; }

        public string? SupersededConnectionId { get; }
    }

    public sealed class Rejected : RoomAdmitOutcome
    {
        public Rejected(string code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
