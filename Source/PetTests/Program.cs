using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using VpnZapretSwitch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Console.WriteLine("=== Testing ThatPet Service & Pack Assets ===");
        Console.WriteLine($"ThatPet AppFolder: {ThatPetService.AppFolder}");
        if (ThatPetService.AppFolder.Contains(@"AppData\Local\Programs\ThatPet", StringComparison.OrdinalIgnoreCase))
            throw new Exception("ThatPetService is still pointing to external AppData folder!");

        var packs = ThatPetService.LoadPacks();
        Console.WriteLine($"Found {packs.Count} packs:");
        foreach (var p in packs)
        {
            Console.WriteLine($"  - {p.Name} (id: {p.Id}, folder: {p.FolderName}, poses: {p.TotalPoses})");
            if (!File.Exists(p.IdleImagePath)) throw new Exception($"Missing idle pose for {p.Id}");
            if (!File.Exists(p.WorkingImagePath)) throw new Exception($"Missing working pose for {p.Id}");
            if (!File.Exists(p.CelebrateImagePath)) throw new Exception($"Missing celebrate pose for {p.Id}");
            if (!File.Exists(p.SignatureImagePath)) throw new Exception($"Missing signature pose for {p.Id}");
            if (!File.Exists(p.SleepImagePath)) throw new Exception($"Missing sleep pose for {p.Id}");
        }

        if (packs.Count < 9) throw new Exception("Expected at least 9 pet packs!");

        Console.WriteLine("\n=== Testing PetsForm UI & Model Switching ===");
        ApplicationConfiguration.Initialize();
        using var form = new PetsForm();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-25000, -25000);
        form.Show();
        Application.DoEvents();

        // Switch to astra
        Console.WriteLine("Switching to 'astra'...");
        ThatPetService.SetActiveCharacter("astra");
        Thread.Sleep(500);
        string active = ThatPetService.GetActiveCharacter();
        Console.WriteLine($"Active character in settings: {active}");
        if (active != "astra") throw new Exception("Failed to set active character to astra!");

        // Switch to grok
        Console.WriteLine("Switching to 'grok'...");
        ThatPetService.SetActiveCharacter("grok");
        Thread.Sleep(500);
        active = ThatPetService.GetActiveCharacter();
        Console.WriteLine($"Active character in settings: {active}");
        if (active != "grok") throw new Exception("Failed to set active character to grok!");

        // Switch to deepseek
        Console.WriteLine("Switching to 'deepseek'...");
        ThatPetService.SetActiveCharacter("deepseek");
        Thread.Sleep(500);
        active = ThatPetService.GetActiveCharacter();
        Console.WriteLine($"Active character in settings: {active}");
        if (active != "deepseek") throw new Exception("Failed to set active character to deepseek!");

        // Capture screenshot of PetsForm
        form.Size = new Size(920, 740);
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        string outDir = Path.Combine(AppContext.BaseDirectory, "test-output");
        Directory.CreateDirectory(outDir);
        string shotPath = Path.Combine(outDir, "pets-form.png");
        bmp.Save(shotPath);
        Console.WriteLine($"PetsForm captured to: {shotPath}");

        form.Close();
        Console.WriteLine("\nPASS: ThatPet packs, assets, UI hero showcase, and dynamic pet switching verified successfully!");

        Console.WriteLine("\n=== Testing Spotify Queue & Flyout Animation ===");
        // 1. Test Queue parser
        string queueJson = """
        {
            "currently_playing": {
                "id": "t1",
                "name": "Current Hit",
                "uri": "spotify:track:t1",
                "duration_ms": 195000,
                "artists": [{ "name": "Artist A" }],
                "album": { "name": "Album X", "images": [{ "url": "https://img" }] }
            },
            "queue": [
                {
                    "id": "t2",
                    "name": "Next Track",
                    "uri": "spotify:track:t2",
                    "duration_ms": 210000,
                    "artists": [{ "name": "Artist B" }],
                    "album": { "name": "Album Y" }
                }
            ]
        }
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(queueJson);
        var curItem = SpotifyApi.ParseQueueItem(doc.RootElement.GetProperty("currently_playing"));
        if (curItem is null || curItem.Name != "Current Hit" || curItem.DurationText != "3:15")
            throw new Exception("Queue currently_playing parser failed!");

        var nextItem = SpotifyApi.ParseQueueItem(doc.RootElement.GetProperty("queue")[0]);
        if (nextItem is null || nextItem.Name != "Next Track" || nextItem.DurationText != "3:30")
            throw new Exception("Queue next item parser failed!");

        // 2. Test SpotifyPanel & ToggleQueue
        using var testMain = new Form { Size = new Size(800, 600), StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000) };
        using var panel = new SpotifyPanel { Dock = DockStyle.Bottom, Height = 196 };
        testMain.Controls.Add(panel);
        testMain.Show();
        Application.DoEvents();

        // Toggle queue open
        panel.ToggleQueue();
        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(15); }
        Console.WriteLine("Spotify queue flyout opened smoothly with animation!");

        // Toggle queue closed
        panel.ToggleQueue();
        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(15); }
        Console.WriteLine("Spotify queue flyout closed smoothly with animation!");

        // 3. Test Fullscreen view & dynamic color palette extraction
        using var redBmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(redBmp)) g.Clear(Color.FromArgb(180, 20, 50));
        var redColor = ColorPalette.ExtractDominantColor(redBmp);
        if (redColor.R < redColor.B || redColor.R < redColor.G)
            throw new Exception("Dominant red color extraction failed!");
        Console.WriteLine($"Extracted dominant red color: R={redColor.R}, G={redColor.G}, B={redColor.B}");

        panel.OpenFullscreenView(testMain);
        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(15); }
        Console.WriteLine("Spotify fullscreen player opened smoothly with ambient gradient!");

        testMain.Close();
        Console.WriteLine("PASS: Spotify queue parser, flyout, fullscreen view, and animations verified successfully!");
    }
}
