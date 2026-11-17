using Microsoft.UI.Dispatching;
using Windows.Gaming.Input;

namespace ZenStationShell.Services;

public enum PadDirection { Left, Right, Up, Down }

/// <summary>Leser første tilkoblede kontroller og gir enkle navigasjonshendelser (med hold-for-gjentakelse).</summary>
public sealed class GamepadInput : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private Gamepad? _gamepad;
    private bool _active = true;
    private GamepadButtons _prev;
    private PadDirection? _heldDir;
    private DateTime _repeatAt;

    public event Action<PadDirection>? Direction;
    public event Action? Accept;
    public event Action? Menu;
    public event Action? View;
    public event Action? Back;
    public event Action? Favorite;
    public event Action? Search;
    public event Action<int>? TabChange;

    public GamepadInput(DispatcherQueue queue)
    {
        _queue = queue;
        _timer = queue.CreateTimer();
        _timer.Tick += (_, _) => Poll();
        _gamepad = Gamepad.Gamepads.FirstOrDefault();
        Gamepad.GamepadAdded += (_, g) => { _gamepad ??= g; _queue.TryEnqueue(Apply); };
        Gamepad.GamepadRemoved += (_, g) =>
        {
            if (ReferenceEquals(_gamepad, g)) _gamepad = Gamepad.Gamepads.FirstOrDefault();
            _queue.TryEnqueue(Apply);
        };

        Apply();
    }

    /// <summary>
    /// Windows gir ikke kontrollerinput til et vindu uten fokus, så vi stopper avlesingen når vinduet er i bakgrunnen
    /// (da kan en kjørende app/et spill få hele CPU-en). Uten kontroller sjekkes det bare fire ganger i sekundet.
    /// </summary>
    public void SetActive(bool active)
    {
        _active = active;
        Apply();
    }

    private void Apply()
    {
        if (!_active) { _timer.Stop(); return; }

        // Knapper som holdes inne når vi våkner skal ikke telle som nye trykk.
        var pad = _gamepad;
        if (!_timer.IsRunning)
        {
            try { _prev = pad?.GetCurrentReading().Buttons ?? default; } catch { _prev = default; }
            _heldDir = null;
        }

        _timer.Interval = TimeSpan.FromMilliseconds(pad is null ? 250 : 16);
        if (!_timer.IsRunning) _timer.Start();
    }

    private void Poll()
    {
        var pad = _gamepad;
        if (pad is null) return;

        var r = pad.GetCurrentReading();
        var pressed = r.Buttons & ~_prev;
        _prev = r.Buttons;

        if (pressed.HasFlag(GamepadButtons.A)) Accept?.Invoke();
        if (pressed.HasFlag(GamepadButtons.Menu)) Menu?.Invoke();
        if (pressed.HasFlag(GamepadButtons.View)) View?.Invoke();
        if (pressed.HasFlag(GamepadButtons.B)) Back?.Invoke();
        if (pressed.HasFlag(GamepadButtons.Y)) Favorite?.Invoke();
        if (pressed.HasFlag(GamepadButtons.X)) Search?.Invoke();
        if (pressed.HasFlag(GamepadButtons.LeftShoulder)) TabChange?.Invoke(-1);
        if (pressed.HasFlag(GamepadButtons.RightShoulder)) TabChange?.Invoke(1);

        PadDirection? dir = null;
        if (r.Buttons.HasFlag(GamepadButtons.DPadLeft) || r.LeftThumbstickX < -0.6) dir = PadDirection.Left;
        else if (r.Buttons.HasFlag(GamepadButtons.DPadRight) || r.LeftThumbstickX > 0.6) dir = PadDirection.Right;
        else if (r.Buttons.HasFlag(GamepadButtons.DPadUp) || r.LeftThumbstickY > 0.6) dir = PadDirection.Up;
        else if (r.Buttons.HasFlag(GamepadButtons.DPadDown) || r.LeftThumbstickY < -0.6) dir = PadDirection.Down;

        if (dir is null)
        {
            _heldDir = null;
            return;
        }

        var now = DateTime.UtcNow;
        if (dir != _heldDir)
        {
            _heldDir = dir;
            _repeatAt = now.AddMilliseconds(380);
            Direction?.Invoke(dir.Value);
        }
        else if (now >= _repeatAt)
        {
            _repeatAt = now.AddMilliseconds(130);
            Direction?.Invoke(dir.Value);
        }
    }

    public void Dispose() => _timer.Stop();
}
