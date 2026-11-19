using Clauderoo.Tray;

namespace Clauderoo.Core.Tests;

[Collection(WinFormsCollection.Name)]
public class UpdateFormTests
{
    static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));

    [Fact]
    public void StatusText_IsVisible_NotSqueezedToNothing()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                // A repo that doesn't exist: the window shows its first step ("Looking for the latest version?").
                using var form = new UpdateForm("nobody-xyz/none-xyz", () => { });
                form.Show();
                for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(20); }
                var labels = All(form).OfType<Label>().Where(l => l.Text.Length > 0).ToList();
                Assert.NotEmpty(labels);
                Assert.All(labels, l => Assert.True(l.Width > 100, $"'{l.Text}' is {l.Width}px wide"));
                form.Close();
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Exception("on the STA thread", failure);
    }
}
