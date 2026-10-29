namespace BorderLess.App;

internal sealed partial class InputRouter
{
    private int lockState;
    private bool swallowEmergencyRelease;
    public void RefreshLocalKeyboardState()
    {
        // MainWindow calls this from its UI/message-loop thread. The low-level
        // keyboard callback runs before Windows updates asynchronous key state.
        lock (gate) if (outgoing == null && incoming == null && pending == null) lockState = desktop.LockState & 7;
    }
    private KeyboardSnapshot CurrentKeyboard()
    {
        var modifiers = 0;
        foreach (var key in physicalKeys.Keys) modifiers |= KeyboardPacket.ModifierBit(key);
        return new KeyboardSnapshot(modifiers, lockState & 7);
    }
    private void SeedPhysicalModifiers()
    {
        foreach (var key in KeyboardPacket.ModifierKeys)
            if (desktop.IsKeyDown(key) && !physicalKeys.ContainsKey(key)) physicalKeys[key] = KeyboardPacket.CanonicalData(key);
    }
    private bool CaptureKeyboardLocked(Native.KeyHook value, bool up)
    {
        var data = ((int)value.Scan & 0xffff) | ((value.Flags & 1) != 0 ? 0x10000 : 0);
        var key = KeyboardPacket.Normalize((int)value.Key, data);
        if ((data & 0xffff) == 0 && KeyboardPacket.IsStateKey(key)) data = KeyboardPacket.CanonicalData(key);
        var alreadyDown = physicalKeys.ContainsKey(key);
        if (up) physicalKeys.Remove(key); else physicalKeys[key] = data;
        var toggle = KeyboardPacket.ToggleBit(key);
        if (!up && !alreadyDown && toggle != 0) lockState ^= toggle;
        if (key == 0x7b)
        {
            var modifiers = CurrentKeyboard().Modifiers;
            if (!up && (modifiers & 12) != 0 && (modifiers & 48) != 0)
            {
                swallowEmergencyRelease = true;
                ReturnLocked("Control local recuperado con Ctrl+Alt+F12."); return true;
            }
            if (up && swallowEmergencyRelease) { swallowEmergencyRelease = false; return true; }
        }
        if (Paused || sharingPause.Locked) return false;
        if (incoming != null) { Diagnostics.Takeover(); ReturnLocked("Se recuperó el control con el teclado local."); return false; }
        if (outgoing == null) return false;
        // Repeated lock-key downs do not change the toggle state a second time.
        if (!up && alreadyDown && toggle != 0) return false;
        var packetData = outgoing.InputRevision >= 2 ? KeyboardPacket.Pack(data, CurrentKeyboard()) : data;
        if (!outgoing.Send(Frame.FromInput(new(up ? InputKind.KeyUp : InputKind.KeyDown, ++sequence, outgoingEpoch, x, y, key, packetData)))) ReturnLocked("La conexión no está disponible.");
        else Diagnostics.Sent();
        // Keep the source's Caps/Num/Scroll state and keyboard LEDs in step.
        return toggle == 0;
    }
    private void SynchronizeKeyboard(List<Native.Input> inputs, KeyboardSnapshot desired, int exceptModifier = 0)
    {
        if (desired.Modifiers is < 0 or > 63 || desired.Locks is < 0 or > 7) throw new InvalidDataException("Estado de teclado no válido.");
        foreach (var modifier in KeyboardPacket.ModifierKeys)
        {
            if (modifier == exceptModifier) continue;
            var on = (desired.Modifiers & KeyboardPacket.ModifierBit(modifier)) != 0;
            if (on && !keys.ContainsKey(modifier))
            {
                var native = KeyboardPacket.CanonicalData(modifier); keys[modifier] = native;
                inputs.Add(Native.Key(modifier, native, false)); Diagnostics.ModifierRepair();
            }
            else if (!on && keys.Remove(modifier, out var native)) { inputs.Add(Native.Key(modifier, native, true)); Diagnostics.ModifierRepair(); }
        }
        foreach (var toggle in KeyboardPacket.ToggleKeys)
        {
            var bit = KeyboardPacket.ToggleBit(toggle);
            if (((lockState ^ desired.Locks) & bit) == 0) continue;
            var native = KeyboardPacket.CanonicalData(toggle);
            inputs.Add(Native.Key(toggle, native, false)); inputs.Add(Native.Key(toggle, native, true));
            Diagnostics.LockRepair();
        }
        lockState = desired.Locks;
    }
    private void ApplyKeyboard(List<Native.Input> inputs, InputEvent input, int revision)
    {
        if (input.Code is < 1 or > 255 || !KeyboardPacket.TryUnpack(input.Data, out var native, out var snapshot) || (snapshot != null && revision < 2)) throw new InvalidDataException("Paquete de teclado no válido.");
        var key = KeyboardPacket.Normalize(input.Code, native);
        var up = input.Kind == InputKind.KeyUp;
        if (snapshot is { } current)
        {
            SynchronizeKeyboard(inputs, current, key);
            // A lock state is applied as one bounded down/up pulse, rather than
            // blindly toggled again on a repeated or recovered CapsLock event.
            if (KeyboardPacket.ToggleBit(key) != 0) return;
        }
        if (!up)
        {
            var already = keys.ContainsKey(key);
            if (already && KeyboardPacket.IsStateKey(key)) return;
            keys[key] = native;
            if (!already && snapshot == null) lockState ^= KeyboardPacket.ToggleBit(key);
            inputs.Add(Native.Key(key, native, false));
        }
        else if (keys.Remove(key, out var held)) inputs.Add(Native.Key(key, held, true));
    }
}
