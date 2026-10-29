namespace BorderLess.App;

// Pause reasons are independent: unlocking must not undo the user's manual
// choice, and a user resume must not clear an active lock policy.
internal sealed class SharingPauseState
{
    public bool Manual { get; private set; }
    public bool Locked { get; private set; }
    public bool PauseOnLock { get; private set; }
    public bool Automatic => Locked && PauseOnLock;
    public bool Paused => Manual || Automatic;
    public SharingPauseState(bool pauseOnLock) => PauseOnLock = pauseOnLock;
    public void SetManual(bool value) => Manual = value;
    public void SetLocked(bool value) => Locked = value;
    public void SetPolicy(bool value) => PauseOnLock = value;
    public string Description => Manual && Automatic ? "Uso compartido pausado manualmente y por bloqueo de Windows." : Manual ? "Uso compartido pausado manualmente." : Automatic ? "Uso compartido pausado por bloqueo de Windows." : "Uso compartido activo.";
    public string Hint => Manual && Automatic ? "Desbloquea Windows y pulsa Reanudar para volver a compartir. La pausa manual se conserva." : Manual ? "Pulsa Reanudar para permitir control, portapapeles y archivos. Los archivos cancelados deben enviarse de nuevo." : Automatic ? "Se reanudará automáticamente al desbloquear Windows." : "";
}
