using System.Drawing;
using CodexUsage.DailyPanel;

var failures = 0;
var checks = 0;
void Check(string name, Rectangle expected, Point anchor, Rectangle work, Size size)
{
    checks++;
    var actual = TrayPopupGeometry.Place(anchor, work, size);
    if (actual != expected)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: expected {expected}, actual {actual}");
    }
    else Console.WriteLine("PASS " + name);
}

// Each literal catches positioning on the wrong side, missing gap, or incorrect clamping.
Check("Bottom taskbar opens above", new(1700, 752, 400, 280), new(1900, 1060), new(0, 0, 2400, 1040), new(400, 280));
Check("Top taskbar opens below", new(1700, 48, 400, 280), new(1900, 20), new(0, 40, 2400, 1040), new(400, 280));
Check("Left taskbar opens right", new(48, 660, 400, 280), new(20, 800), new(40, 0, 2360, 1080), new(400, 280));
Check("Right taskbar opens left", new(1952, 660, 400, 280), new(2380, 800), new(0, 0, 2360, 1080), new(400, 280));
Check("Bottom right clamps to work area", new(1992, 752, 400, 280), new(2399, 1060), new(0, 0, 2400, 1040), new(400, 280));
Check("Right taskbar near bottom stays on its left side", new(1952, 792, 400, 280), new(2380, 1070), new(0, 0, 2360, 1080), new(400, 280));
Check("Bottom taskbar near left stays above", new(8, 752, 400, 280), new(5, 1060), new(0, 0, 2400, 1040), new(400, 280));
Check("Negative monitor origin remains negative", new(-1912, -328, 400, 280), new(-1900, -20), new(-1920, -1080, 1920, 1040), new(400, 280));
Check("Inside anchor chooses nearest edge", new(752, 360, 400, 280), new(1150, 500), new(0, 0, 1160, 1080), new(400, 280));
Check("Auto hidden taskbar cursor inside work chooses bottom", new(1992, 792, 400, 280), new(2390, 1079), new(0, 0, 2400, 1080), new(400, 280));
Check("Tiny work area reduces size without escaping", new(-20, -10, 12, 9), new(-8, 3), new(-20, -10, 12, 9), new(400, 280));
Check("Narrow work area removes impossible margins", new(0, 72, 10, 20), new(5, 120), new(0, 0, 10, 100), new(10, 20));

foreach (var item in new[] { (Dpi: 96u, Want: new Size(400, 281)), (Dpi: 144u, Want: new Size(600, 422)), (Dpi: 192u, Want: new Size(800, 562)) })
{
    checks++;
    var actual = TrayPopupGeometry.ScaleFromDip(400, 281, item.Dpi);
    if (actual != item.Want) { failures++; Console.WriteLine($"FAIL {item.Dpi} DPI: expected {item.Want}, actual {actual}"); }
    else Console.WriteLine($"PASS {item.Dpi} DPI physical size");
}
Check("150 percent popup is placed in physical pixels", new(1792, 650, 600, 422), new(2390, 1100), new(0, 0, 2400, 1080), new(600, 422));

void Interaction(string name, bool expected, bool actual)
{
    checks++;
    if (expected == actual) Console.WriteLine("PASS " + name);
    else { failures++; Console.WriteLine($"FAIL {name}: expected {expected}, actual {actual}"); }
}
var clicks = new TrayPopupInteraction();
Interaction("First tray click opens", true, clicks.ShouldOpen(false, 1000));
Interaction("Tray click while visible toggles closed", false, clicks.ShouldOpen(true, 1100));
Interaction("Next distinct click reopens", true, clicks.ShouldOpen(false, 1200));
clicks.Dismissed(true, 2000);
Interaction("Tray mouse down deactivation consumes its pending click", false, clicks.ShouldOpen(false, 2010));
Interaction("Suppressed click does not consume the next distinct click", true, clicks.ShouldOpen(false, 2100));
clicks.Dismissed(false, 3000);
Interaction("Outside dismissal allows next tray click immediately", true, clicks.ShouldOpen(false, 3010));
clicks.Dismissed(true, 4000);
Interaction("Stale tray suppression expires", true, clicks.ShouldOpen(false, 4501));
clicks.Dismissed(true, 5000);
clicks.Dismissed(false, 5010);
Interaction("Ordinary dismissal clears previous suppression", true, clicks.ShouldOpen(false, 5020));
clicks.Dismissed(true, 6000);
Interaction("Suppression includes the 500ms boundary", false, clicks.ShouldOpen(false, 6500));
clicks.Dismissed(true, 7000);
Interaction("Clock reversal does not suppress a later click", true, clicks.ShouldOpen(false, 6999));

Console.WriteLine($"{checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
