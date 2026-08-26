// Which role the current person is using the app as - specialist (recording the constant
// reference) or trainee (a normal session, compared against that reference later). Plain static
// utility, not a MonoBehaviour, so it's reachable from any scene without an Inspector reference -
// same pattern CalibrationManager already uses for CalibrationCorrectionLocal/IsCalibrated,
// since Inspector references can't cross scene files.
public static class SessionRoleManager
{
    public static bool IsSpecialist { get; private set; }

    public static void SetRole(bool isSpecialist)
    {
        IsSpecialist = isSpecialist;
    }
}
