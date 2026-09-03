using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.AspNetCore.Builder;

namespace Wordbook;

public class AppHost : IDisposable
{
    public Config Config { get; }
    public DataStore Store { get; }
    public DictClient Dict { get; }
    public AiClient Ai { get; }
    public Pipeline Pipe { get; }
    private WebApplication _app;

    public AppHost(Config cfg)
    {
        Config = cfg;
        Store = new DataStore(cfg);
        Dict = new DictClient();
        Ai = new AiClient(cfg);
        Pipe = new Pipeline(Store, Dict, Ai);
    }

    public void Start()
    {
        _app = WebHost.Build(Config, Store, Dict, Ai, Pipe);
        _ = _app.StartAsync();
    }

    public void Dispose()
    {
        try { _app?.StopAsync().Wait(3000); } catch { /* 忽略 */ }
        try { ((IDisposable)_app)?.Dispose(); } catch { /* 忽略 */ }
    }
}

public class TrayApp : ApplicationContext
{
    private readonly Config _cfg;
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly HotkeyWindow _hotkey;
    private readonly CaptureHost _captureHost = new();
    private ToolStripMenuItem _autoStartItem;
    private readonly string _exePath = Environment.ProcessPath ?? "";

    public TrayApp(Config cfg, AppHost host, bool openHome)
    {
        Diag.Log("TrayApp starting");
        _cfg = cfg;
        _host = host;
        host.Start();

        _icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "生词本 - Ctrl+Alt+Shift+W 记录",
            Visible = true,
        };
        BuildMenu();
        _hotkey = new HotkeyWindow(OpenCapture);
        _ = _hotkey.Handle;
        Diag.Log("TrayApp ready, hotkey handle created");

        if (openHome)
        {
            _icon.BalloonTipTitle = "生词本已就绪";
            _icon.BalloonTipText = "在任何地方按 Ctrl+Alt+Shift+W 记录生词；单击图标打开词库。";
            _icon.ShowBalloonTip(3000);
            System.Windows.Forms.Timer openTimer = new() { Interval = 600 };
            openTimer.Tick += (_, _) => { openTimer.Stop(); OpenHome(); };
            openTimer.Start();
        }
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开生词本", null, (_, _) => OpenHome());
        menu.Items.Add("手动记录一个词", null, (_, _) => OpenCapture());
        menu.Items.Add(new ToolStripSeparator());
        _autoStartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart())
        {
            Checked = StartupEnabled(),
            CheckOnClick = true,
        };
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenHome();
    }

    private void OpenHome()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_cfg.HomeUrl) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    private void OpenCapture()
    {
        Diag.Log("OpenCapture invoked by hotkey");
        var prev = Native.GetForegroundWindow();
        try { _captureHost.Toggle(_cfg, prev); }
        catch (Exception ex)
        {
            Diag.Log("OpenCapture error: " + ex);
            _icon.ShowBalloonTip(4000, "生词本", "记录弹窗启动失败，详见日志：" + Diag.LogPath, ToolTipIcon.Error);
        }
    }

    private bool StartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return string.Equals(key?.GetValue("Wordbook") as string, QuoteExe(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void ToggleAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_autoStartItem.Checked) key.SetValue("Wordbook", QuoteExe());
            else key.DeleteValue("Wordbook", false);
        }
        catch { _autoStartItem.Checked = !_autoStartItem.Checked; }
    }

    private string QuoteExe() => "\"" + _exePath + "\"";

    private void ExitApp()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _hotkey.Release();
        _captureHost.Stop();
        _host.Dispose();
        ExitThread();
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(91, 91, 240));
            g.FillEllipse(brush, 0, 0, 32, 32);
            using var pen = new Pen(Color.White, 2.4f);
            g.DrawArc(pen, 7, 8, 8, 16, 210, 230);
            g.DrawArc(pen, 17, 8, 8, 16, -80, 230);
            g.DrawLine(pen, 16, 8, 16, 24);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

public class HotkeyWindow : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x4A01;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_ALT = 0x0001;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_NOREPEAT = 0x4000;
    private readonly Action _callback;

    public HotkeyWindow(Action callback)
    {
        _callback = callback;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-100, -100);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Native.RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.W))
        {
            // 主热键被占用时退化为 Ctrl+Alt+Shift+K
            Native.RegisterHotKey(Handle, HOTKEY_ID + 1, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.K);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) _callback();
        base.WndProc(ref m);
    }

    public void Release()
    {
        try { Native.UnregisterHotKey(Handle, HOTKEY_ID); Native.UnregisterHotKey(Handle, HOTKEY_ID + 1); } catch { /* 忽略 */ }
        Dispose();
    }
}

internal static class WordList
{
    internal static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","be","to","of","and","a","in","that","have","i","it","for","not","on","with","he","as","you","do","at",
        "this","but","his","by","from","they","we","say","her","she","or","an","will","my","one","all","would","there",
        "their","what","so","up","out","if","about","who","get","which","go","me","when","make","can","like","time","no",
        "just","him","know","take","people","into","year","your","good","some","could","them","see","other","than","then",
        "now","look","only","come","its","over","think","also","back","after","use","two","how","our","work","first","well",
        "way","even","new","want","because","any","these","give","day","most","us","am","is","are","was","were","been",
        "has","had","did","does","doing","having","being","before","between","under","again","where","while","still","own",
        "same","too","very","through","against","during","without","much","should","may","might","must","shall","ever",
        "since","each","few","those","both","such","here","there","why","how","more","less","most","least","off","down",
        "long","little","great","right","left","around","every","another","always","never","often","sometimes","already",
        "says","said","get","got","getting","going","went","come","came","coming","make","made","making","see","saw",
        "seen","know","knew","known","think","thought","say","said","take","took","taken","put","put","thing","things",
        "something","anything","nothing","everything","someone","anyone","everyone","everyone","nobody","somebody","maybe",
        "probably","really","quite","rather","although","though","unless","except","whether","whatever","however",
        "therefore","because","so","hence","thus","like","just","also","too","very","really","almost","even","still",
        "away","near","far","above","below","inside","outside","before","after","ago","soon","early","late","today",
        "tomorrow","yesterday","now","then","next","last","first","second","third","many","much","more","most","some",
        "any","all","both","each","either","neither","few","several","lot","lots","plenty","enough","etc","etcetera",
        "ok","okay","well","fine","yes","no","not","never","none","nothing","nobody","please","thanks","thank","hello",
        "hi","hey","oh","ah","uh","hmm","wow","great","good","bad","new","old","big","small","large","little","high",
        "low","long","short","wide","deep","high","strong","weak","fast","slow","hard","easy","simple","complex",
        "important","interesting","different","same","other","another","main","major","minor","common","rare","normal",
        "usual","special","particular","general","specific","certain","sure","clear","clean","full","empty","open",
        "close","closed","start","stop","begin","end","finish","done","use","used","using","need","want","like","love",
        "hate","help","let","allow","make","cause","lead","bring","take","get","give","put","keep","hold","show","tell",
        "ask","answer","call","talk","speak","read","write","listen","hear","watch","look","see","find","feel","think",
        "know","understand","remember","forget","learn","teach","study","work","play","run","walk","move","come","go",
        "stay","leave","return","turn","change","become","seem","appear","look","sound","feel","taste","smell",
        "happen","occur","exist","live","die","grow","develop","build","create","make","produce","form","break",
        "cut","open","close","push","pull","lift","drop","throw","catch","hit","kick","touch","hold","carry","wear",
        "dress","eat","drink","cook","bake","sleep","wake","rest","sit","stand","lie","lay","rise","fall","jump",
        "swim","fly","drive","ride","travel","visit","meet","join","leave","follow","lead","guide","direct","point",
        "face","turn","return","send","receive","accept","refuse","allow","permit","forbid","prevent","protect",
        "include","exclude","contain","hold","cover","hide","show","display","reveal","explain","describe","define",
        "compare","contrast","relate","connect","link","separate","divide","combine","mix","match","fit","suit",
        "belong","own","possess","lack","need","require","demand","expect","hope","wish","desire","prefer","choose",
        "select","pick","decide","determine","solve","fix","repair","correct","improve","better","worsen","increase",
        "decrease","raise","lower","add","subtract","multiply","divide","count","measure","weigh","test","check",
        "verify","confirm","prove","disprove","agree","disagree","argue","debate","discuss","consider","examine",
        "investigate","explore","search","find","discover","invent","create","design","plan","prepare","arrange",
        "organize","manage","control","operate","run","conduct","perform","execute","implement","apply","practice",
        "train","exercise","practise","rehearse","repeat","copy","imitate","follow","obey","comply","resist","fight",
        "defend","attack","win","lose","beat","defeat","succeed","fail","try","attempt","strive","struggle","suffer",
        "endure","bear","stand","tolerate","accept","reject","ignore","neglect","abandon","desert","leave","quit",
        "stop","cease","pause","continue","proceed","progress","advance","move","shift","transfer","transport","carry",
        "deliver","send","mail","post","phone","call","contact","reach","arrive","depart","start","begin","initiate",
        "launch","establish","found","create","set","place","put","position","locate","situate","install","remove",
        "delete","erase","cancel","undo","redo","save","store","keep","preserve","maintain","retain","update","refresh",
        "change","modify","adjust","adapt","convert","transform","translate","interpret","express","communicate",
        "state","claim","assert","declare","announce","report","inform","notify","warn","advise","suggest","recommend",
        "propose","offer","present","introduce","represent","symbolize","indicate","suggest","imply","mean","signify",
        "denote","refer","mention","quote","cite","reference","source","origin","cause","reason","basis","foundation",
        "result","effect","impact","influence","affect","consequence","outcome","product","output","input","process",
        "method","approach","technique","system","way","means","manner","style","fashion","form","type","kind","sort",
        "category","class","group","set","series","sequence","order","arrangement","structure","organization",
        "function","purpose","role","position","status","state","condition","situation","circumstance","context",
        "background","environment","setting","scene","place","location","area","region","country","city","town",
        "village","home","house","building","room","door","window","wall","floor","ceiling","roof","ground","floor",
        "street","road","path","way","route","direction","side","part","piece","bit","section","segment","portion",
        "amount","quantity","number","figure","value","price","cost","expense","charge","fee","rate","ratio","percent",
        "percentage","average","total","sum","total","whole","entire","complete","full","partial","half","quarter",
        "third","fraction","share","portion","unit","item","element","component","part","factor","variable",
        "characteristic","feature","quality","property","attribute","aspect","detail","point","issue","matter",
        "topic","subject","theme","question","problem","difficulty","challenge","obstacle","barrier","limit","boundary",
        "edge","border","range","scope","extent","degree","level","grade","rank","class","standard","measure",
        "criterion","basis","base","foundation","support","backing","aid","assistance","help","support","service",
        "facility","resource","material","substance","thing","object","item","article","goods","product","tool",
        "instrument","device","machine","apparatus","equipment","supply","stock","store","supply","provision",
        "information","data","knowledge","understanding","awareness","idea","concept","notion","thought","view",
        "opinion","belief","attitude","feeling","emotion","sense","perception","impression","memory","experience",
        "event","incident","occurrence","case","example","instance","sample","model","pattern","example","specimen",
        "proof","evidence","sign","indication","clue","hint","suggestion","advice","guidance","instruction","direction",
        "rule","regulation","law","policy","principle","doctrine","theory","hypothesis","assumption","supposition",
        "guess","estimate","prediction","forecast","projection","calculation","computation","measurement","analysis",
        "examination","inspection","investigation","study","research","survey","review","evaluation","assessment",
        "judgment","decision","conclusion","finding","result","discovery","invention","creation","production",
        "development","growth","progress","improvement","advancement","success","achievement","accomplishment",
        "goal","aim","objective","target","purpose","intention","plan","scheme","project","program","initiative",
        "strategy","tactic","policy","approach","tactics","procedure","operation","action","activity","behavior",
        "conduct","practice","habit","custom","tradition","culture","society","community","public","people","person",
        "individual","human","man","woman","child","boy","girl","family","parent","mother","father","brother",
        "sister","friend","enemy","stranger","neighbor","colleague","partner","associate","member","participant",
        "leader","head","chief","boss","manager","director","president","king","queen","government","authority",
        "power","control","influence","responsibility","duty","obligation","task","job","work","labor","effort",
        "energy","strength","power","force","might","ability","capability","capacity","skill","talent","gift",
        "genius","knowledge","wisdom","intelligence","mind","brain","thought","idea","imagination","creativity",
        "passion","interest","hobby","activity","pastime","leisure","free","busy","active","passive","eager",
        "willing","ready","prepared","able","capable","competent","efficient","effective","productive","useful",
        "helpful","beneficial","valuable","important","essential","necessary","required","vital","critical","crucial",
        "key","major","primary","main","chief","principal","significant","substantial","considerable","remarkable",
        "notable","striking","impressive","amazing","wonderful","fantastic","excellent","outstanding","superb",
        "terrific","marvelous","splendid","magnificent","beautiful","lovely","pretty","handsome","attractive",
        "charming","pleasant","nice","kind","friendly","warm","gentle","soft","smooth","tender","harsh","rough",
        "hard","tough","difficult","easy","simple","complex","complicated","involved","sophisticated","advanced",
        "modern","contemporary","current","present","recent","latest","new","novel","fresh","original","unique",
        "distinct","different","diverse","various","multiple","numerous","several","countless","many","plenty",
        "enough","sufficient","adequate","satisfactory","acceptable","proper","appropriate","suitable","fitting",
        "correct","right","accurate","exact","precise","specific","particular","certain","definite","positive",
        "sure","certain","confident","clear","obvious","evident","apparent","visible","noticeable","prominent",
        "famous","well-known","renowned","popular","common","widespread","universal","general","typical","usual",
        "normal","standard","regular","ordinary","average","typical","representative","characteristic","typical",
        "constant","continuous","continual","endless","infinite","eternal","permanent","temporary","brief","short",
        "quick","fast","rapid","swift","slow","gradual","sudden","abrupt","sharp","steep","gentle","flat","level",
        "straight","curved","bent","crooked","irregular","uneven","smooth","rough","coarse","fine","delicate",
        "fragile","weak","strong","powerful","mighty","potent","intense","extreme","severe","serious","grave",
        "acute","critical","dangerous","risky","unsafe","safe","secure","protected","guarded","vulnerable",
        "exposed","susceptible","prone","liable","subject","immune","resistant","tolerant","sensitive","delicate",
        "aware","conscious","mindful","attentive","careful","cautious","wary","vigilant","alert","watchful",
        "careless","reckless","negligent","sloppy","messy","untidy","tidy","neat","clean","orderly","organized",
        "systematic","methodical","logical","rational","reasonable","sensible","sound","valid","legitimate",
        "proper","appropriate","acceptable","right","correct","wrong","false","incorrect","mistaken","erroneous",
        "invalid","illegal","unlawful","legal","lawful","permissible","allowed","permitted","forbidden","banned",
        "prohibited","restricted","limited","constrained","confined","bound","tied","connected","linked","related",
        "associated","relevant","pertinent","applicable","appropriate","suitable","compatible","consistent",
        "agreeable","favorable","positive","negative","neutral","balanced","unbiased","objective","subjective",
        "personal","individual","private","public","shared","common","collective","social","political","economic",
        "financial","commercial","industrial","technical","scientific","academic","educational","cultural",
        "historical","traditional","conventional","established","accepted","recognized","acknowledged","admitted",
        "confessed","denied","refused","rejected","accepted","approved","endorsed","supported","opposed","against",
        "for","pro","anti","contrary","opposite","reverse","inverse","inverted","backward","forward","onward",
        "outward","inward","upward","downward","sideways","diagonal","vertical","horizontal","parallel","perpendicular",
        "equal","unequal","similar","alike","identical","same","equivalent","comparable","analogous","corresponding",
        "matching","twin","duplicate","copy","original","authentic","genuine","real","true","actual","realistic",
        "practical","theoretical","hypothetical","speculative","abstract","concrete","specific","tangible","visible",
        "virtual","digital","online","offline","local","remote","distant","far","near","close","adjacent","neighboring",
        "surrounding","nearby","approximate","rough","exact","precise","accurate","detailed","comprehensive",
        "extensive","broad","wide","narrow","limited","restricted","confined","focused","concentrated","intensive",
        "extensive","thorough","complete","full","partial","incomplete","unfinished","incomplete","missing","absent",
        "present","existing","current","former","previous","prior","earlier","later","subsequent","following",
        "upcoming","forthcoming","imminent","approaching","distant","remote","far-off","nearby","close","immediate",
        "instant","immediate","direct","indirect","straight","roundabout","circuitous","meandering","winding",
        "twisting","curving","turning","bending","folding","wrinkled","creased","smooth","flat","level","even",
        "uniform","consistent","constant","steady","stable","fixed","firm","solid","hard","rigid","stiff","inflexible",
        "flexible","elastic","plastic","malleable","adaptable","versatile","multipurpose","useful","practical",
        "functional","operational","working","active","functioning","broken","damaged","defective","faulty","flawed",
        "imperfect","incomplete","deficient","inadequate","insufficient","lacking","wanting","needing","requiring",
        "essential","fundamental","basic","underlying","core","central","key","principal","major","dominant",
        "predominant","leading","foremost","primary","main","chief","supreme","paramount","overriding","uppermost",
        "highest","top","best","worst","least","greatest","smallest","largest","biggest","tiniest","youngest",
        "oldest","newest","latest","earliest","soonest","fastest","slowest","strongest","weakest","simplest",
        "easiest","hardest","most","least","very","extremely","highly","deeply","greatly","vastly","immensely",
        "tremendously","enormously","hugely","really","quite","fairly","rather","somewhat","slightly","a bit",
        "a little","barely","hardly","scarcely","rarely","seldom","occasionally","frequently","often","usually",
        "normally","generally","typically","commonly","rarely","seldom","infrequently","never","always","ever",
        "sometimes","regularly","constantly","continuously","repeatedly","again","once","twice","thrice",
        "together","apart","separately","individually","jointly","collectively","alone","lonely","single",
        "married","single","alone","together","with","without","beside","besides","except","excluding",
        "including","among","amongst","between","within","inside","outside","throughout","across","through",
        "along","around","round","about","toward","towards","onto","upon","underneath","beneath","behind",
        "beyond","past","ahead","front","back","side","top","bottom","middle","center","centre","edge","corner",
        "beginning","start","end","finish","middle","half","quarter","third","two","three","four","five","six",
        "seven","eight","nine","ten","eleven","twelve","thirteen","fourteen","fifteen","sixteen","seventeen",
        "eighteen","nineteen","twenty","thirty","forty","fifty","sixty","seventy","eighty","ninety","hundred",
        "thousand","million","billion","trillion","zero","one","two","three","four","five","six","seven","eight",
        "nine","ten","eleven","twelve","first","second","third","fourth","fifth","sixth","seventh","eighth",
        "ninth","tenth","last","final","initial","beginning","opening","closing","ending","concluding",
    };
}

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
}
