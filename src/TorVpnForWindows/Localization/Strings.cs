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
    public static string StateReconnecting = "Yeniden bağlanılıyor";
    public static string StateWaitingForNetwork = "Ağ bekleniyor";
    public static string StateFailed = "Başarısız";

    public static string HintDisconnected = "Trafiğiniz normal şekilde çıkıyor.";
    public static string HintBlockedUntilConnected =
        "İnternet engellendi. Tor bağlanana kadar hiçbir program dışarı çıkamaz.";
    public static string HintConnected = "Bu bilgisayardaki her bağlantı Tor üzerinden geçiyor.";
    public static string HintReconnecting = "Bağlantı baştan kuruluyor.";
    public static string HintReconnectingBlocked =
        "Bağlantı baştan kuruluyor. Tor bağlanana kadar internet engelli.";
    public static string HintWaitingForNetwork = "Bir ağa bağlanınca Tor'a bağlanılacak.";
    public static string HintWaitingForNetworkBlocked =
        "Bir ağa bağlanınca Tor'a bağlanılacak. O zamana kadar internet engelli.";

    // ---- status card ----
    public static string StatusEntryAddress = "Giriş adresi";
    public static string StatusExitAddress = "Çıkış adresi";
    public static string StatusExitCountry = "Çıkış ülkesi";
    public static string StatusChecking = "Denetleniyor…";
    public static string StatusUnknown = "Bilinmiyor";
    public static string StatusNotConfirmed = "check.torproject.org bu adresi Tor çıkışı olarak tanımıyor";
    public static string StatusConfirmed = "Tor'a bağlanıldığı check.torproject.org tarafından doğrulandı";
    public static string StatusDownload = "İndirme";
    public static string StatusUpload = "Yükleme";
    public static string StatusTotalFormat = "Toplam {0}";
    public static string StatusMapUnknown = "Çıkış ülkesi belirlenemedi";
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
    public static string MeekFrontLabel = "Görünen adres";
    public static string MeekFrontHint =
        "meek, bağlantıyı bu sitenin adı altında kurar. Ağı izleyen taraf bu adı görür, Tor'a dair bir şey görmez. Karışık seçildiğinde her denemede sıra karışır.";
    public static string MeekFrontDefault = "Tor Project'in yayınladığı";
    public static string MeekFrontMixed = "Karışık";

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

    // ---- settings: program lists ----
    public static string SettingInternetLists = "İnternet erişimi";
    public static string SettingInternetListsHint =
        "Güvenlik duvarı gibi çalışır, Tor bağlı olsa da olmasa da geçerlidir. Beyaz liste açıkken yalnızca listedeki programlar internete çıkabilir. Kara liste açıkken listedeki programlar internete hiç çıkamaz. İkisi aynı anda açık olamaz.";
    public static string SettingTunnelLists = "Tünel";
    public static string SettingTunnelListsHint =
        "Beyaz liste açıkken yalnızca listedeki programlar Tor'dan geçer, diğerleri normal bağlantıdan çıkar. Kara liste açıkken listedeki programlar normal bağlantıdan çıkar, diğerleri Tor'dan geçer. İkisi aynı anda açık olamaz.";
    public static string ListWhite = "Beyaz liste";
    public static string ListBlack = "Kara liste";
    public static string ListEdit = "Düzenle";
    public static string ListCountFormat = "{0} program";
    public static string ListCountNone = "Program yok";

    // ---- program list window ----
    public static string ListWindowInternetWhite = "İnternet beyaz listesi";
    public static string ListWindowInternetWhiteHint = "Beyaz liste açıkken yalnızca bu programlar internete çıkabilir.";
    public static string ListWindowInternetBlack = "İnternet kara listesi";
    public static string ListWindowInternetBlackHint = "Kara liste açıkken bu programlar internete hiç çıkamaz.";
    public static string ListWindowTunnelWhite = "Tünel beyaz listesi";
    public static string ListWindowTunnelWhiteHint = "Beyaz liste açıkken yalnızca bu programlar Tor'dan geçer.";
    public static string ListWindowTunnelBlack = "Tünel kara listesi";
    public static string ListWindowTunnelBlackHint = "Kara liste açıkken bu programlar Tor'dan geçmez, normal bağlantıdan çıkar.";
    public static string ListWindowMatchNote =
        "Programlar tam exe konumuna göre eşleşir. Aynı adlı başka bir konumdaki program bu listeden etkilenmez.";
    public static string ListWindowListed = "LİSTEDEKİ PROGRAMLAR";
    public static string ListWindowRunning = "AÇIK PROGRAMLAR";
    public static string ListWindowSearch = "Ara";
    public static string ListWindowRefresh = "Yenile";
    public static string ListWindowAdd = "Ekle";
    public static string ListWindowRemove = "Kaldır";
    public static string ListWindowAlreadyListed = "Listede";
    public static string ListWindowBrowse = "Exe dosyası seç…";
    public static string ListWindowClose = "Kapat";
    public static string ListWindowEmpty =
        "Bu listede program yok. Aşağıdaki açık programlardan ya da exe dosyası seçerek ekleyebilirsiniz.";
    public static string ListWindowLoading = "Açık programlar okunuyor…";
    public static string ListWindowNoMatch = "Aramaya uyan açık program yok.";
    public static string ListWindowFileMissing = "Bu konumda dosya yok";
    public static string ListWindowFileFilter = "Programlar (*.exe)|*.exe";
    public static string ListWindowCannotResolve = "Şu dosyanın konumu okunamadı: {0}";

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
