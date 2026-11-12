using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA2;   // Logik je Delphi – UIA2 radi bolje, isto kao u RezToInter

namespace ProveriSlobodneKolicine
{
    // Isti format kao appsettings.json iz RezToInter-a
    public class AppConfig
    {
        public string LogikPutanja { get; set; } = @"C:\Program Files (x86)\Logik\FirmA1\firma.exe";
        public string KorisnickoIme { get; set; } = "";
        public string Lozinka { get; set; } = "";
    }

    internal class Program
    {
        static Application logikApp;
        const string NAZIV_CHECKBOX = "Samo slobodna";
        static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "log.txt");

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        static int Main(string[] args)
        {
            using var mutex = new Mutex(true, "ProveriSlobodneKolicine", out bool prvi);
            if (!prvi) { Log("Već radi druga instanca, izlazim."); return 1; }

            try
            {
                Log("Start");
                Mouse.MovePixelsPerMillisecond = 20;
                var cfg = UcitajKonfiguraciju();

                using var automation = new UIA2Automation();

                Window main = PokreniILogujSe(automation, cfg);   // posle logina smo već na Dokumentima
                Window dlg = OtvoriOpcije(automation, main);
                IzaberiTab(dlg, "Opcije dokumenata 1");

                bool promenjeno = ProveriSlobodnuKolicinu(dlg);
                ZatvoriOpcije(automation, dlg, promenjeno);

                Log(promenjeno
                    ? "OK – bilo je odštiklirano, uključeno i sačuvano (U redu)."
                    : "OK – već je bilo uključeno, ništa nije menjano (Otkaži).");
                return 0;
            }
            catch (Exception ex)
            {
                Log("GREŠKA: " + ex);
                return 2;
            }
            finally
            {
                try
                {
                    if (logikApp != null && !logikApp.HasExited)
                    {
                        logikApp.Close();
                        Thread.Sleep(2000);
                        if (!logikApp.HasExited) logikApp.Kill();
                        Log("Logik zatvoren");
                    }
                }
                catch (Exception ex) { Log("Greška pri zatvaranju: " + ex.Message); }
            }
        }

        // appsettings.json pored exe-a – prekopiraj ga iz RezToInter foldera
        static AppConfig UcitajKonfiguraciju()
        {
            string putanja = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(putanja))
                throw new Exception("Nema appsettings.json pored exe-a (prekopiraj ga iz RezToInter-a)");
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(putanja))
                   ?? throw new Exception("appsettings.json nije ispravan");
        }

        // 1. Start firma.exe → glavni prozor → Ctrl+F5 (Dokumenti) → Prijava → login
        static Window PokreniILogujSe(UIA2Automation automation, AppConfig cfg)
        {
            logikApp = Application.Launch(cfg.LogikPutanja);
            Log("Pokrenut Logik, PID " + logikApp.ProcessId);

            var main = Retry.WhileNull(
                () => NadjiProzor(automation, w => (w.Name ?? "").Contains("Logik")),
                TimeSpan.FromSeconds(30)).Result
                ?? throw new Exception("Glavni prozor Logika se nije pojavio");
            Thread.Sleep(1500);   // da se Delphi prozor do kraja iscrta

            IntPtr h = main.Properties.NativeWindowHandle.Value;
            ShowWindow(h, 3);     // maximize
            SetForegroundWindow(h);
            Thread.Sleep(300);

            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.F5);
            Log("Poslat Ctrl+F5 (Dokumenti)");

            var prijava = Retry.WhileNull(
                () => NadjiProzor(automation, w => (w.Name ?? "").Contains("Prijava")),
                TimeSpan.FromSeconds(10)).Result
                ?? throw new Exception("Prozor 'Prijava' se nije pojavio posle Ctrl+F5");

            // isto kao PerformLogicLogin u RezToInter-u
            var polja = prijava.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            var dugmad = prijava.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
            polja[0].AsTextBox().Text = cfg.Lozinka;
            polja[1].AsTextBox().Text = cfg.KorisnickoIme;
            dugmad[1].Click();
            Log($"Prijava kao {cfg.KorisnickoIme}");

            Retry.WhileTrue(
                () => NadjiProzor(automation, w => (w.Name ?? "").Contains("Prijava")) != null,
                TimeSpan.FromSeconds(5));
            Thread.Sleep(1000);
            Log("Ulogovan, otvoreni Dokumenti");
            return main;
        }

        // 2. Opcije u toolbaru + čekanje prozora "Opcije"
        static Window OtvoriOpcije(UIA2Automation automation, Window main)
        {
            SetForegroundWindow(main.Properties.NativeWindowHandle.Value);
            Thread.Sleep(200);

            Cekaj(() => main.FindFirstDescendant(cf => cf.ByName("Opcije").And(cf.ByControlType(ControlType.Button))),
                  10, "dugme 'Opcije' u toolbaru")
                .Click();

            var dlg = Retry.WhileNull(
                () => NadjiProzor(automation, w => w.Name == "Opcije"),
                TimeSpan.FromSeconds(10)).Result
                ?? throw new Exception("Prozor 'Opcije' se nije otvorio");

            Log("Otvoren prozor Opcije");
            return dlg;
        }

        // ===== Win32 deo: UIA2 ne vidi unutrašnje tabove i checkbox-ove u Opcijama =====
        const int WM_GETTEXT = 0x000D, BM_GETCHECK = 0x00F0, BM_CLICK = 0x00F5;
        const int TCM_GETITEMCOUNT = 0x1304, TCM_GETCURSEL = 0x130B, TCM_SETCURFOCUS = 0x1330;

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, StringBuilder l);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        class Kontrola { public IntPtr H; public string Klasa = ""; public string Tekst = ""; }

        static List<Kontrola> Deca(IntPtr root)
        {
            var lista = new List<Kontrola>();
            EnumChildWindows(root, (h, l) =>
            {
                var k = new StringBuilder(256);
                GetClassName(h, k, k.Capacity);
                var t = new StringBuilder(512);
                SendMessage(h, WM_GETTEXT, (IntPtr)t.Capacity, t);
                lista.Add(new Kontrola { H = h, Klasa = k.ToString(), Tekst = t.ToString() });
                return true;
            }, IntPtr.Zero);
            return lista;
        }

        static Kontrola NadjiCheckbox(IntPtr dlgH) =>
            Deca(dlgH).FirstOrDefault(k => k.Klasa == "TCheckBox" &&
                k.Tekst.Replace("&", "").StartsWith(NAZIV_CHECKBOX, StringComparison.OrdinalIgnoreCase));

        static bool CheckboxVidljiv(IntPtr dlgH)
        {
            var cb = NadjiCheckbox(dlgH);
            return cb != null && IsWindowVisible(cb.H);
        }

        // 3. Tab "Opcije dokumenata 1" – preko TCM_SETCURFOCUS (Delphi dobije normalnu promenu taba)
        static void IzaberiTab(Window dlg, string nazivTaba)
        {
            IntPtr dlgH = dlg.Properties.NativeWindowHandle.Value;
            if (CheckboxVidljiv(dlgH)) { Log("Tab je već otvoren"); return; }

            // Unutrašnji TPageControl je onaj sa najviše tabova (spoljni ima samo "Opšte")
            var pc = Deca(dlgH).Where(k => k.Klasa == "TPageControl")
                               .Select(k => new { k.H, Broj = SendMessage(k.H, TCM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32() })
                               .OrderByDescending(x => x.Broj).FirstOrDefault()
                     ?? throw new Exception("Nije nađen TPageControl u Opcijama");
            Log($"Unutrašnji TPageControl ima {pc.Broj} tabova, trenutno izabran {SendMessage(pc.H, TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero)}");

            // Očekujemo indeks 3, ali probamo redom sve dok checkbox ne postane vidljiv
            var redosled = new[] { 3 }.Concat(Enumerable.Range(0, pc.Broj).Where(i => i != 3));
            foreach (int i in redosled)
            {
                SendMessage(pc.H, TCM_SETCURFOCUS, (IntPtr)i, IntPtr.Zero);
                Thread.Sleep(600);
                if (CheckboxVidljiv(dlgH)) { Log($"Izabran tab {nazivTaba} (indeks {i})"); return; }
            }

            IspisiWin32(dlgH);
            throw new Exception($"Nisam uspeo da otvorim tab '{nazivTaba}' (spisak kontrola je u log-u)");
        }

        // 4. Checkbox – vraća true ako je morao da ga uključi
        static bool ProveriSlobodnuKolicinu(Window dlg)
        {
            IntPtr dlgH = dlg.Properties.NativeWindowHandle.Value;
            var cb = NadjiCheckbox(dlgH) ?? throw new Exception("Nije nađen checkbox 'Samo slobodna količina'");

            bool Stiklirano() => SendMessage(cb.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1;

            Log($"Stanje '{cb.Tekst}': {(Stiklirano() ? "štiklirano" : "NIJE štiklirano")}");
            if (Stiklirano()) return false;

            SetForegroundWindow(dlgH);
            Thread.Sleep(150);
            PostMessage(cb.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(500);

            if (!Stiklirano())   // rezerva: pravi klik mišem na kvadratić
            {
                GetWindowRect(cb.H, out RECT r);
                Mouse.Click(new System.Drawing.Point(r.Left + 7, (r.Top + r.Bottom) / 2));
                Thread.Sleep(500);
            }
            if (!Stiklirano()) throw new Exception("Ne mogu da uključim checkbox");

            Log("Checkbox uključen");
            return true;
        }

        static void IspisiWin32(IntPtr root)
        {
            Log("---- Win32 kontrole prozora Opcije ----");
            foreach (var k in Deca(root).Take(400))
                Log($"  {k.Klasa} | '{k.Tekst}' | vidljiv={IsWindowVisible(k.H)}");
            Log("---------------------------------------");
        }

        // 5. U redu ako je menjano, inače Otkaži
        static void ZatvoriOpcije(UIA2Automation automation, Window dlg, bool sacuvaj)
        {
            string dugme = sacuvaj ? "U redu" : "Otkaži";
            Cekaj(() => dlg.FindFirstDescendant(cf => cf.ByName(dugme).And(cf.ByControlType(ControlType.Button))),
                  5, $"dugme '{dugme}'")
                .Click();

            Retry.WhileTrue(() => NadjiProzor(automation, w => w.Name == "Opcije") != null, TimeSpan.FromSeconds(5));
            Log($"Kliknuto {dugme}");
        }

        // Top-level prozori samo NAŠEG Logika (ne dira druge otvorene Logike)
        static Window NadjiProzor(UIA2Automation automation, Func<AutomationElement, bool> uslov)
        {
            foreach (var w in automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                try
                {
                    if (w.Properties.ProcessId.ValueOrDefault != logikApp.ProcessId) continue;
                    if (uslov(w)) return w.AsWindow();
                }
                catch { }
            }
            return null;
        }

        static AutomationElement Cekaj(Func<AutomationElement> nadji, int sekundi, string opis) =>
            Retry.WhileNull(nadji, TimeSpan.FromSeconds(sekundi)).Result
            ?? throw new Exception($"Nije nađeno: {opis}");

        static void Log(string poruka)
        {
            string linija = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {poruka}";
            Console.WriteLine(linija);
            File.AppendAllText(LogPath, linija + Environment.NewLine);
        }
    }
}
