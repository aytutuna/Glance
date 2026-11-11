namespace Glance;

class OverlayForm : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;

    public OverlayForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }

    // the popup only takes focus when the user clicks its text box
    protected void SetActivatable(bool on)
    {
        const int GWL_EXSTYLE = -20;
        var style = Native.GetWindowLongPtr(Handle, GWL_EXSTYLE);
        Native.SetWindowLongPtr(Handle, GWL_EXSTYLE, on ? style & ~WS_EX_NOACTIVATE : style | WS_EX_NOACTIVATE);
    }

    // only from Windows 10 2004 on; before that the popup is waited out instead of excluded
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Native.CanExcludeFromCapture)
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
