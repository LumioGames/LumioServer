namespace Lumio.Server.MvpHost.Admission;

public interface IAdmissionCredentialVerifier
{
    AdmissionCredentialOutcome Verify(string admissionCredential);
}
