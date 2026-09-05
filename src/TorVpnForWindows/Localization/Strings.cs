namespace TorVpnForWindows.Localization;

/// <summary>
/// User-facing UI strings, Turkish by default. <see cref="LocManager"/> reflection-writes these
/// fields to <c>lang.tr.xml</c> when it is missing, and — for any other chosen language —
/// overwrites the fields from <c>lang.&lt;code&gt;.xml</c> (e.g. the shipped <c>lang.en.xml</c>).
///
/// To localize a new string: add a public static string field here and a matching
/// &lt;s name="FieldName"&gt; entry in lang.en.xml. Fields are mutable on purpose (reflection sets
/// them); never mark them readonly/const. Interpolated text uses {0},{1}… with string.Format.
/// </summary>
internal static class Strings
{
    // ---- window ----
    public static string AppTitle = "Tor VPN for Windows";
    public static string AppSubtitle = "Tüm trafik Tor üzerinden";

    // ---- tabs ----
    public static string TabStatus = "Durum";
    public static string TabSettings = "Ayarlar";
    public static string TabLog = "Günlük";

    // ---- buttons ----
    public static string ButtonConnect = "Bağlan";
    public static string ButtonDisconnect = "Bağlantıyı kes";
    public static string ButtonCancel = "Vazgeç";
    public static string ButtonRetry = "Yeniden dene";
    public static string ButtonNewIdentity = "Yeni devre";

    // ---- connection states ----
    public static string StateDisconnected = "Bağlı değil";
    public static string StatePreparing = "Hazırlanıyor";
    public static string StateBootstrapping = "Tor ağına bağlanılıyor";
    public static string StateEstablishingTunnel = "Tünel kuruluyor";
    public static string StateConnected = "Bağlandı";
    public static string StateDisconnecting = "Bağlantı kesiliyor";
    public static string StateInterrupted = "Trafik engellendi";
    public static string StateFailed = "Başarısız";

    public static string HintDisconnected = "Trafiğiniz normal şekilde çıkıyor.";
    public static string HintConnected = "Bu bilgisayardaki her bağlantı Tor üzerinden geçiyor.";
    public static string HintInterrupted = "Tor durdu. Kill switch trafiği tutuyor.";

    // ---- status card ----
    public static string StatusExitAddress = "Çıkış adresi";
    public static string StatusExitCountry = "Çıkış ülkesi";
    public static string StatusChecking = "Denetleniyor…";
    public static string StatusUnknown = "Bilinmiyor";
    public static string StatusNotConfirmed = "Tor Project bu bağlantıyı doğrulayamadı.";
    public static string StatusConfirmed = "Tor Project tarafından doğrulandı";
    public static string StatusDownload = "Alınan";
    public static string StatusUpload = "Gönderilen";
    public static string StatusUdpNotice = "Tor yalnızca TCP taşır. UDP reddedilir, uygulamalar TCP'ye döner.";

    // ---- settings: section headers ----
    public static string SectionConnection = "BAĞLANTI";
    public static string SectionNetwork = "AĞ";
    public static string SectionApplication = "UYGULAMA";
    public static string SectionAdvanced = "GELİŞMİŞ";

    // ---- settings: language ----
    public static string SettingLanguage = "Dil";
    public static string LanguageSystem = "Windows'u izle";

    // ---- settings: exit country ----
    public static string SettingExitCountry = "Çıkış ülkesi";
    public static string ExitCountryAny = "Farketmez";
    public static string SettingExitCountryHint =
        "Tor yalnızca bu ülkedeki çıkış rölelerini kullanır. Az röle bulunan bir ülke seçmek bağlanmayı yavaşlatır, hatta engelleyebilir.";

    // ---- settings: bridges ----
    public static string SettingBridges = "Köprüler";
    public static string BridgeNone = "Kapalı";
    public static string BridgeObfs4 = "obfs4";
    public static string BridgeSnowflake = "Snowflake";
    public static string BridgeMeek = "meek";
    public static string BridgeCustom = "Kendi listem";
    public static string SettingBridgesHint =
        "Köprüler, Tor kullandığınızı ağınızı yöneten taraftan gizler. Yerleşik listeler Tor Project'ten çekilip önbelleğe alınır.";
    public static string SettingBridgesCustomHint =
        "Her satıra bir köprü satırı. bridges.torproject.org adresinden alabilirsiniz.";

    // ---- settings: toggles ----
    public static string SettingKillSwitch = "Bağlantı düştüğünde interneti engelle";
    public static string SettingKillSwitchHint =
        "Tor bağlantısı yokken tüm internet kapalı kalır. Ağ geri geldiğinde de Tor'a bağlanana kadar kapalı kalmaya devam eder.";
    public static string SettingStrictRoute = "Sıkı yönlendirme";
    public static string SettingStrictRouteHint =
        "Diğer tüm bağdaştırıcılarda DNS'i engeller. Yalnızca VirtualBox gibi bir şeyi bozuyorsa kapatın.";
    public static string SettingAllowLan = "Yerel ağa izin ver";
    public static string SettingAllowLanHint =
        "Bağlıyken yazıcı, ağ deposu ve modeminize erişilebilir kalır.";
    public static string SettingAutoConnect = "Açılışta bağlan";
    public static string SettingMinimizeToTray = "Kapatınca bildirim alanına in";
    public static string SettingStartMinimized = "Simge durumunda başlat";

    // ---- settings: exclusions ----
    public static string SettingExclusions = "Tünel dışı uygulamalar";
    public static string SettingExclusionsHint =
        "Bu programların trafiği tüneli atlar ve isim çözümlemesini olağan DNS sunucularından yapar.";
    public static string ExclusionsOpen = "Listeyi aç";
    public static string ExclusionsCountFormat = "{0} program tünel dışında";
    public static string ExclusionsNone = "Tünel dışı program yok";

    // ---- settings: advanced ----
    public static string SettingTunName = "Bağdaştırıcı adı";
    public static string SettingMtu = "MTU";
    public static string SettingRestartNeeded = "Değişiklikler bir sonraki bağlantıda geçerli olur.";

    // ---- log tab ----
    public static string LogCopy = "Kopyala";
    public static string LogClear = "Temizle";
    public static string LogOpenFolder = "Klasörü aç";
    public static string LogAutoScroll = "Otomatik kaydır";

    // ---- tray ----
    public static string TrayShow = "Aç";
    public static string TrayConnect = "Bağlan";
    public static string TrayDisconnect = "Bağlantıyı kes";
    public static string TrayExit = "Çık";
    public static string TrayTipConnected = "Tor VPN: bağlı";
    public static string TrayTipDisconnected = "Tor VPN: bağlı değil";

    // ---- messages ----
    public static string ErrorTitle = "Bir sorun çıktı";
    public static string ErrorAlreadyRunning = "Tor VPN for Windows zaten çalışıyor.";
    public static string ErrorNeedsAdmin =
        "Bu program ağ bağdaştırıcısını oluşturmak için yönetici yetkisine ihtiyaç duyar.";
    public static string ConfirmQuitTitle = "Çık";
    public static string ConfirmQuitWhileConnected =
        "Tünel açık. Çıkmak bağlantıyı kesecek. Devam edilsin mi?";
}
