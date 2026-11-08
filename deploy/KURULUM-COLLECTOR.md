# BerMap Collector Kurulumu

Collector, BerMap sunucusunun doğrudan erişemediği ağlarda (şube, DMZ, ayrı bir forest) Active Directory ve vCenter keşfini yerinde yapar ve sonucu **dışarı yönlü HTTPS** ile sunucuya gönderir. Sunucudan collector'a hiçbir bağlantı açılmaz; collector'da gelen bağlantı için port açmanız gerekmez. Sunucuya ulaşılamadığında keşif sonuçları yerel kuyrukta tutulur ve bağlantı gelince sırayla iletilir; servis gönderim sırasında durdurulursa kalan parçalar da kuyruğa yazılır ve yeniden başlayınca gönderilir. Bir snapshot'ın parçalarından biri kaybolursa (kuyruk kotası, yeniden deneme sınırı, sunucunun reddi) snapshot hiçbir zaman "tam" sayılmaz: kalan parçaları gönderilmez ve sunucudaki iş `COLLECTOR_BATCH_LOST` / `COLLECTOR_BATCH_REJECTED` hatasıyla kapanır.

Paket self-contained'dir; collector makinesinde .NET kurulumu gerekmez.

## Ön koşullar

- Kurulu ve çalışan bir **BerMap sunucusu** ve collector makinesinden sunucuya HTTPS erişimi (ör. `https://bermap.corp.local`).
- **Collector kayıt token'ı.** Sunucu kurulum betiği sonunda yazar; sunucu yapılandırmasında `BerMap:Collectors:EnrollmentToken` değeridir (Windows'ta `C:\ProgramData\BerMap\appsettings.Production.json`, Linux'ta `/etc/bermap/appsettings.Production.json`).
- **Windows Server 2019+** (x64) ya da **systemd kullanan Linux** (x64, arm64). Linux kurulum betiği `python3` kullanır.
- Keşfedilecek kaynaklara collector makinesinden salt okunur erişim: Active Directory için LDAPS 636, vCenter için HTTPS 443.

## Windows Server

`BerMap-Collector-<sürüm>-win-x64.zip` paketini açın, **yönetici PowerShell** penceresinde paket klasörüne geçin:

```powershell
.\Install-BerMapCollector.ps1 -ServerUrl https://bermap.corp.local -Name dc2-collector
```

Betik kayıt token'ını sorar (ekranda gösterilmez), dosyaları `C:\Program Files\BerMap\Collector` altına kopyalar, yapılandırmayı `C:\ProgramData\BerMap\Collector\appsettings.Production.json` dosyasına yazar, **BerMapCollector** Windows servisini kaydeder (servisin `BERMAP_CONFIG_FILE` ortam değişkeni bu dosyayı gösterir) ve collector sunucuya kaydolana kadar bekler. Veri dizinine (yapılandırma, collector kimliği, çevrimdışı kuyruk) yalnız SYSTEM ve Administrators erişebilir; dizinin ve yapılandırma dosyasının sahibi Administrators yapılır. Kurulumdan önce başka bir kullanıcının oluşturduğu (sahibi SYSTEM ya da Administrators olmayan) bir veri dizini ya da yapılandırma dosyası reddedilir: sahibi izinleri sonradan geri açabilir. İçeriği inceleyip dizini silin ya da `-Force` ile betiğin sahipliği alıp izinleri sıfırlamasını sağlayın. Token'ı sormadan vermek için `-EnrollmentToken (Read-Host -AsSecureString)` kullanın.

| Parametre | Varsayılan | Açıklama |
|---|---|---|
| `-ServerUrl` | — | BerMap sunucusunun adresi (`https://sunucu[:port]`; sunucu bir ters vekilde yol önekiyle yayınlanıyorsa `https://vekil/bermap` gibi önekle) |
| `-EnrollmentToken` | — | Kayıt token'ı (SecureString); verilmezse ilk kurulumda sorulur |
| `-Name` | makine adı | Konsolda görünen collector adı |
| `-InstallDir`, `-DataDir` | `Program Files\BerMap\Collector`, `ProgramData\BerMap\Collector` | Program dizini; yapılandırma ve veri dizini. Ya henüz var olmamalı (betik oluşturur), ya daha önce bu betikle kurulmuş olmalı, ya da boş varsayılan dizin olmalıdır; başka bir dizin (boş olsa bile, ör. `C:\Tools`) reddedilir. Sunucunun veri dizini (`ProgramData\BerMap`, kasa) collector veri dizini olarak kullanılamaz |
| `-RegistrationTimeoutSeconds` | `120` | Kaydın beklenme süresi |
| `-Force` | — | Başka bir kullanıcının oluşturduğu veri dizininin ve yapılandırma dosyasının sahipliğini alır, izinlerini sıfırlar |

**Yükseltme:** yeni paketi açıp betiği parametresiz yeniden çalıştırın. Yapılandırma birleştirilir: eklediğiniz entegrasyonlar ve elle yazdığınız ayarlar korunur, yalnız verdiğiniz değerler değişir. `-ServerUrl`, `-EnrollmentToken` ya da `-Name` değişirse collector yeniden kaydolur (yeni ad konsolda yeni bir kayıt olarak görünür; eskisini **Collectorlar** sayfasından silin).

**Kaldırma:** `.\Uninstall-BerMapCollector.ps1` (yapılandırma ve collector kimliği korunur, yeniden kurulum aynı kimlikle devam eder; veri dizinini de silmek için `-RemoveData`). Kaldırma yalnız kurulumun oluşturduğunu siler: betiğin oluşturduğu dizin kaldırılır, kurulumdan önce boş olarak var olan varsayılan dizin boşaltılıp bırakılır. `-RemoveData` yalnız collector veri dizinini (`.bermap-collector-data` işareti, `collector-id` ya da `offline_queue` içeren) siler; sunucunun veri dizinine hiçbir zaman dokunmaz.

## Linux (systemd)

```bash
tar xzf bermap-collector-<sürüm>-linux-x64.tar.gz
cd bermap-collector-<sürüm>-linux-x64
sudo BERMAP_ENROLLMENT_TOKEN='<token>' ./install.sh --server-url https://bermap.corp.local --name dc2-collector
```

Program `/opt/bermap/collector`, yapılandırma `/etc/bermap/collector.json` (`root:bermap-collector`, `0640`), veri `/var/lib/bermap/collector` altına kurulur; servis `bermap-collector` adıyla `bermap-collector` kullanıcısıyla çalışır ve yapılandırmayı systemd biriminin `BERMAP_CONFIG_FILE` ortam değişkeninden bulur. Token `BERMAP_ENROLLMENT_TOKEN` ile verilmezse sorulur; `--enrollment-token` da kullanılabilir ama komut satırı diğer kullanıcılarca `ps` ile görülebilir. Betik sonunda token'ın yalnız ilk karakterlerini yazar. Kayıt en çok 120 saniye beklenir (`BERMAP_REGISTER_TIMEOUT` ile değiştirilebilir). `--server-url` sunucu bir ters vekilde yol önekiyle yayınlanıyorsa öneki de içerebilir (`https://vekil.corp.local/bermap`); collector tüm istekleri bu önekin altına gönderir. `--install-dir` ve `--data-dir` ya henüz var olmamalı (betik oluşturur), ya daha önce bu betikle kurulmuş olmalı, ya da boş varsayılan dizin olmalıdır; başka bir dizin (boş olsa bile, ör. `/opt` ya da `/srv`) reddedilir. Sunucunun veri dizini (`/var/lib/bermap`, kasa) ya da onu içeren bir dizin `--data-dir` olarak reddedilir. Günlükler: `journalctl -u bermap-collector`.

**Yükseltme:** yeni paketle `sudo ./install.sh` (parametresiz). Windows'taki gibi yapılandırma birleştirilir; `--server-url`, token ya da `--name` değişirse collector yeniden kaydolur.
**Kaldırma:** `sudo ./uninstall.sh` (yapılandırma ve veri korunur; `--remove-data` ikisini de siler, gizli dosyalarınıza dokunmaz). Yalnız kurulumun oluşturduğu silinir: betiğin oluşturduğu dizin kaldırılır, kurulumdan önce boş olarak var olan varsayılan dizin boşaltılıp bırakılır; collector işareti (`.bermap-collector-data`, `collector-id`, `offline_queue`) taşımayan ya da sunucuya ait bir veri dizini silinmez.

Collector ile sunucu aynı makinedeyse dizinler birbirinden ayrıdır; sunucu betikleri (`--remove-data` / `-RemoveData` dahil) collector'ın yapılandırmasına ve verisine dokunmaz.

## Kurulumu doğrulama

Kurulum betiği, collector sunucuya kaydolup veri dizinine `collector-id` dosyasını yazana kadar bekler; olmazsa sunucu adresini, token'ı ve günlüğü kontrol etmenizi söyleyen bir hatayla durur. Konsolda **Collectorlar** sayfasında collector verdiğiniz adla **Çevrimiçi** görünür; heartbeat 30 saniyede bir gelir, 90 saniye gelmezse **Çevrimdışı** olur. Sürüm, desteklenen adaptörler, CPU/bellek ve kuyruktaki mesaj sayısı aynı sayfadadır.

## Entegrasyon ekleme

Collector yalnız yapılandırmasındaki entegrasyonları keşfeder. Her entegrasyon önce sunucuda **collector yönetimli** olarak tanımlanır, sonra collector yapılandırmasına bir blok olarak eklenir.

### 1. Sunucuda entegrasyonu oluşturun

Konsolda **Entegrasyonlar → Yeni entegrasyon** ile Active Directory ya da VMware entegrasyonu oluşturun ve **"Veri bir collector üzerinden gelir"** seçeneğini işaretleyin. Kartta **Collector yönetimli** etiketi görünür; sunucu bu kaynağa hiç bağlanmaz, bağlantı testi ve keşif collector'da yapılır. Sunucu, kullanıcı ve parola alanları burada opsiyoneldir; asıl değerler collector'dadır. Collector yönetimli olmayan bir entegrasyona collector'ın gönderdiği veriler sunucu tarafından reddedilir (`NOT_COLLECTOR_MANAGED`).

Entegrasyonun kimliği (GUID) collector yapılandırmasında gerekir. API'den alınabilir:

```bash
TOKEN=$(curl -fsS -X POST https://bermap.corp.local/api/auth/login -H 'content-type: application/json' \
  -d '{"username":"admin","password":"..."}' | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')
curl -fsS https://bermap.corp.local/api/integrations -H "Authorization: Bearer $TOKEN" \
  | python3 -c 'import json,sys; [print(i["id"], i["name"]) for i in json.load(sys.stdin)]'
```

### 2. Parolayı gizli dosyaya yazın

Parola yapılandırma dosyasına yazılmaz (`Configuration` içinde `password` olan blok reddedilir); ayrı bir dosyada durur. Dosyada **yalnız parola** bulunur; sondaki satır sonu yok sayılır. Collector dosyayı her keşiften önce yeniden okur, parola değiştirince servisi yeniden başlatmak gerekmez.

**Linux** — dosya `root:bermap-collector`, `0640` olmalı; servis `/home` altını göremez (`ProtectHome`), dosyayı `/etc/bermap` altında tutun:

```bash
sudo install -d -m 750 -o root -g bermap-collector /etc/bermap/collector-secrets
sudo sh -c 'umask 027; cat > /etc/bermap/collector-secrets/vcenter-dc2'   # parolayı yazın, Enter, Ctrl-D
sudo chown root:bermap-collector /etc/bermap/collector-secrets/vcenter-dc2
sudo chmod 640 /etc/bermap/collector-secrets/vcenter-dc2
```

**Windows** — veri dizinindeki bir dosya, dizinin korumalı izinlerini (yalnız SYSTEM ve Administrators) devralır:

```powershell
$dir = 'C:\ProgramData\BerMap\Collector\secrets'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$secret = Read-Host 'vCenter parolası' -AsSecureString
[IO.File]::WriteAllText("$dir\vcenter-dc2.txt", [Net.NetworkCredential]::new('', $secret).Password)
icacls "$dir\vcenter-dc2.txt"   # yalnız NT AUTHORITY\SYSTEM ve BUILTIN\Administrators görünmeli
```

Dosya yerine bir ortam değişkeni de kullanılabilir (`SecretEnvironmentVariable`); değişken servisin ortamında tanımlı olmalıdır (Linux'ta `systemctl edit bermap-collector` ile `Environment=`, Windows'ta servisin kayıt defterindeki `Environment` değeri).

### 3. Entegrasyon bloğunu yapılandırmaya ekleyin

Yapılandırma dosyasında (`/etc/bermap/collector.json` ya da `C:\ProgramData\BerMap\Collector\appsettings.Production.json`) `BerMap:Collector:Integrations` listesine bir blok ekleyin. Örnek (kurulumun yazdığı alanlar ve iki entegrasyon):

```json
{
  "BerMap": {
    "ServerUrl": "https://bermap.corp.local",
    "Collector": {
      "EnrollmentToken": "…",
      "DataDirectory": "/var/lib/bermap/collector",
      "Name": "dc2-collector",
      "Integrations": [
        {
          "IntegrationId": "3f0c6a8e-6f2b-4d1e-9a51-0d7c2b9e4a10",
          "AdapterType": "VMware",
          "Schedule": "0 */6 * * *",
          "RunOnStart": true,
          "TimeoutSeconds": 1800,
          "Configuration": {
            "server": "vcenter-dc2.corp.local",
            "username": "svc-bermap-ro@vsphere.local"
          },
          "SecretFile": "/etc/bermap/collector-secrets/vcenter-dc2"
        },
        {
          "IntegrationId": "8b1d2c34-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
          "AdapterType": "ActiveDirectory",
          "Schedule": "30 2 * * *",
          "Configuration": {
            "server": "dc01.dc2.corp.local",
            "baseDn": "DC=dc2,DC=corp,DC=local",
            "username": "svc-bermap-ro@dc2.corp.local"
          },
          "SecretFile": "/etc/bermap/collector-secrets/ad-dc2"
        }
      ]
    }
  }
}
```

Windows'ta JSON içinde ters bölü iki kez yazılır: `"SecretFile": "C:\\ProgramData\\BerMap\\Collector\\secrets\\vcenter-dc2.txt"`.

| Alan | Zorunlu | Açıklama |
|---|---|---|
| `IntegrationId` | evet | Sunucudaki collector yönetimli entegrasyonun kimliği (GUID) |
| `AdapterType` | evet | `ActiveDirectory` ya da `VMware` |
| `Schedule` | hayır | Beş alanlı cron, collector makinesinin yerel saatiyle (varsayılan `0 */6 * * *`) |
| `RunOnStart` | hayır | Servis başlarken bir kez keşif (varsayılan `true`) |
| `TimeoutSeconds` | hayır | Bir keşfin üst sınırı, 30–86400 (varsayılan `1800`) |
| `Configuration` | evet | Sunucudaki entegrasyon sihirbazının yazdığı ayarlar: vCenter için `server`, `port`, `username`, `allowUntrustedCertificate`…; AD için `server`, `port`, `useSsl`, `baseDn`, `username`… Parola burada yer almaz |
| `SecretFile` / `SecretEnvironmentVariable` | biri | Parolanın bulunduğu dosya ya da ortam değişkeni (ikisi birden değil) |

Ardından servisi yeniden başlatın: `sudo systemctl restart bermap-collector` ya da `Restart-Service BerMapCollector`. Hatalı bir blok servisi durdurmaz; yalnız o blok atlanır ve nedeni günlüğe yazılır (`journalctl -u bermap-collector`, Windows'ta Uygulama olay günlüğü). Günlükte parola hiçbir zaman yer almaz.

### 4. Konsolda izleyin

- **Collectorlar** sayfası: collector'ın durumu (Çevrimiçi/Çevrimdışı), son heartbeat, sürümü, desteklenen adaptörler ve kuyruktaki mesaj sayısı.
- **Entegrasyonlar** sayfası: entegrasyon kartında **Collector yönetimli** etiketi ve son keşif durumu. **Keşif İşleri** listesinde collector'ın her çalışması bir iş olarak görünür (isteyen: `collector`); iş ayrıntısında varlık/ilişki sayıları, hata ve uyarılar vardır. Collector yönetimli entegrasyonlar konsoldan test edilmez ve başlatılmaz; keşif collector'daki takvimle ya da servisi yeniden başlatarak (`RunOnStart`) tetiklenir.

## Sorun giderme

| Belirti | Neden / çözüm |
|---|---|
| Kurulum "did not register" ile duruyor | Sunucu adresi bu makineden erişilemiyor ya da token yanlış. `curl https://bermap.corp.local/health` ile erişimi, sunucu yapılandırmasındaki `BerMap:Collectors:EnrollmentToken` ile token'ı karşılaştırın. |
| Günlükte `registration returned 401` | Token sunucudakiyle aynı değil. `sudo BERMAP_ENROLLMENT_TOKEN=... ./install.sh` ya da `-EnrollmentToken` ile yenileyin. |
| Günlükte `registration returned 503` | Sunucuda `BerMap:Collectors:EnrollmentToken` tanımlı değil (collector kaydı kapalı). |
| Entegrasyonun işi `NOT_COLLECTOR_MANAGED` / `INTEGRATION_DISABLED` ile reddediliyor | Sunucuda entegrasyonu düzenleyip "Veri bir collector üzerinden gelir" seçeneğini işaretleyin ya da entegrasyonu etkinleştirin. |
| `The secret file ... could not be read` | Dosya yok ya da servis hesabı okuyamıyor (Linux'ta `root:bermap-collector 0640` ve dizin `0750`). |
| Collector **Çevrimdışı** | Servis durmuş ya da sunucuya erişim kesilmiş. Collector çalışmaya devam eder, sonuçları kuyrukta tutar ve bağlantı gelince gönderir. |
| Keşif işi `COLLECTOR_BATCH_LOST` ile başarısız | Kesinti sırasında kuyruk disk kotası (`QueueMaxDiskMb`, varsayılan 500 MB) dolduğu için ya da parça yeniden deneme sınırını aştığı için bir snapshot parçası kayboldu. Eksik snapshot uygulanmaz (düğümler eskimiş işaretlenmez); bir sonraki keşif tam snapshot gönderir. Uzun kesintiler bekleniyorsa kotayı büyütün. |
| Keşif işi `COLLECTOR_BATCH_REJECTED` ile başarısız | Sunucu bir parçayı reddetti (hata metninde durum kodu ve neden yazar; ör. `413` gövde sınırı). Parça boyutu `BerMap:Collector:MaxBatchMb` (varsayılan 8 MB), `BatchSize` (düğüm, varsayılan 500) ve `RelationshipBatchSize` (ilişki, varsayılan 5000) ile küçültülebilir. |

## Güvenlik notları

- Sunucu adresi için `https://` kullanın; `http://` ile token ve envanter şifrelenmeden taşınır (betik uyarır). Sunucu sertifikası collector makinesinde güvenilir olmalıdır.
- Kayıt token'ı tüm collector'lar için ortaktır ve yapılandırma dosyasında durur; dosya yalnız servis hesabı ve yöneticilerce okunabilir. Token'ı değiştirmek için sunucu yapılandırmasındaki değeri yenileyip sunucu servisini yeniden başlatın, ardından her collector'da kurulum betiğini yeni token ile çalıştırın (collector yeniden kaydolur, kimliği ve konsoldaki kaydı korunur).
- Kaynak sistemlerde salt okunur hesaplar kullanın (vCenter'da Read-Only rolü, AD'de sıradan bir kullanıcı); adaptörler kaynaklara hiçbir yazma yapmaz.
- Paketler kod imzalı değildir; `SHA256SUMS.txt` ile bütünlüğü doğrulayın.
