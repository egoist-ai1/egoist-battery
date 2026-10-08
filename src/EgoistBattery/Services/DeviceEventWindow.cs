using Forms = System.Windows.Forms;

namespace EgoistBattery.Services;

// Невидимое нативное окно получает события USB без запуска графического стека WPF.
internal sealed class DeviceEventWindow : Forms.NativeWindow, IDisposable
{
    private readonly Action changed;
    public DeviceEventWindow(Action changed)
    {
        this.changed = changed;
        CreateHandle(new Forms.CreateParams { Caption = "Egoist Battery · события устройств", Style = unchecked((int)0x80000000), Width = 0, Height = 0 });
    }
    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == 0x0219) changed();
        base.WndProc(ref message);
    }
    public void Dispose() => DestroyHandle();
}
