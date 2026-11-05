# BerMap Sunucu Kurulumu

Bu paket BerMap sunucusunu (API, keşif işçisi, zamanlayıcı ve web konsolu) içerir. Paket self-contained'dir; sunucuda .NET kurulumu gerekmez.

## Ön koşullar

- **PostgreSQL 16+**, BerMap sunucusundan erişilebilir olmalı. Veritabanını oluşturma yetkisi olan bir kullanıcı yeterlidir; BerMap ilk açılışta veritabanını ve şemayı kurar. Yetki yoksa önce kullanıcıya ait boş bir `bermap` veritabanı oluşturun.
  ```sql
  CREATE ROLE bermap LOGIN PASSWORD '...' CREATEDB;
  ```
- **Windows Server 2019+** (x64) ya da **systemd kullanan Linux** (x64). Linux kurulum betiği `python3` ve `curl` kullanır.
- Kaynak sistemlere salt okunur erişim: Active Directory için LDAPS 636, vCenter için HTTPS 443.

## Windows Server

Paketi açın, **yönetici PowerShell** penceresinde paket klasörüne geçin:

```powershell
.\Install-BerMapServer.ps1 -DbHost localhost -DbUser bermap
```

Betik veritabanı parolasını sorar, dosyaları `C:\Program Files\BerMap\Server` altına kopyalar, yapılandırmayı program dizininin dışına, `C:\ProgramData\BerMap\appsettings.Production.json` dosyasına yazar (yalnız SYSTEM ve Administrators okuyabilir), **BerMapServer** Windows servisini kaydeder (servisin `BERMAP_CONFIG_FILE` ortam değişkeni bu dosyayı gösterir), güvenlik duvarında portu açar ve sunucu hazır olana kadar bekler.

| Parametre | Varsayılan | Açıklama |
|---|---|---|
| `-Port` | `8080` | Dinlenecek port |
| `-CertificatePath`, `-CertificatePassword` | — | `.pfx` verilirse konsol HTTPS ile sunulur |
| `-DbHost`, `-DbPort`, `-DbName`, `-DbUser`, `-DbPassword` | `5432`, `bermap` | PostgreSQL bağlantısı |
| `-InstallDir`, `-DataDir` | `Program Files\BerMap\Server`, `ProgramData\BerMap` | Program dizini; yapılandırma ve kasa dizini. Program dizini boş olmalı ya da daha önce bu betikle kurulmuş olmalıdır |

**Yükseltme:** yeni paketi açıp betiği parametresiz yeniden çalıştırın. Yapılandırma, kasa ve veritabanı korunur; elle eklediğiniz ayarlar (ör. `BerMap:ReverseProxy`) silinmez. Yalnız verdiğiniz değerler değişir: `-Db*` parametreleri bağlantı dizesindeki ilgili alanı (`-DbPassword` tek başına parolayı yeniler), `-Port` yalnız portu, `-CertificatePath` (ve `-CertificatePassword`) yalnız sertifikayı. Örneğin sertifika yenilemek için:

```powershell
.\Install-BerMapServer.ps1 -CertificatePath C:\certs\yeni.pfx -CertificatePassword (Read-Host -AsSecureString)
```

**Kaldırma:** `.\Uninstall-BerMapServer.ps1` (yapılandırma, kasa ve veritabanı korunur, yeniden kurulum aynı collector token'ı ile devam eder; `ProgramData\BerMap` dizinini de silmek için `-RemoveData`).

## Linux (systemd)

```bash
tar xzf bermap-server-<sürüm>-linux-x64.tar.gz
cd bermap-server-<sürüm>-linux-x64
sudo ./install.sh --db-host localhost --db-user bermap
```

Program `/opt/bermap/server`, yapılandırma `/etc/bermap/appsettings.Production.json`, kasa `/var/lib/bermap/vault` altına kurulur; servis `bermap-server` adıyla `bermap` kullanıcısıyla çalışır ve yapılandırmayı systemd biriminin `BERMAP_CONFIG_FILE` ortam değişkeninden bulur. Parolayı sormadan vermek için `BERMAP_DB_PASSWORD` ortam değişkenini kullanın. `--install-dir` boş bir dizin ya da daha önce bu betikle kurulmuş bir dizin olmalıdır; betik başka içerik barındıran bir dizini (ör. `/opt`) silmez, kurulumu reddeder. Günlükler: `journalctl -u bermap-server`.

**Yükseltme:** yeni paketle `sudo ./install.sh` (parametresiz). Yapılandırma birleştirilir; elle eklenen ayarlar korunur, yalnız verilen değerler değişir: `--db-host`, `--db-port`, `--db-name`, `--db-user` bağlantı dizesindeki ilgili alanı, `--port` dinlenen portu değiştirir. Veritabanı parolasını yenilemek için `sudo BERMAP_DB_PASSWORD=... ./install.sh` yeterlidir.
**Kaldırma:** `sudo ./uninstall.sh` (yapılandırma, kasa ve veritabanı korunur; `--remove-data` kasayı ve yapılandırmayı da siler).

## İlk giriş

Kurulum sonunda betik konsol adresini, ilk yönetici (`admin`) parolasını ve collector kayıt token'ını yazar. Parola kasa dizinindeki `initial-admin-password.txt` dosyasındadır; ilk girişten sonra **Kullanıcılar** ekranından yeni parola belirleyip bu dosyayı silin.

## Güvenlik notları

- Konsol varsayılan olarak düz HTTP ile sunulur. Üretimde `-CertificatePath` ile HTTPS kullanın ya da IIS/nginx gibi TLS sonlandıran bir ters vekilin arkasına alın. Ters vekil kullanıyorsanız adresini `BerMap:ReverseProxy:KnownProxies` ayarına ekleyin (Windows'ta `C:\ProgramData\BerMap\appsettings.Production.json`, Linux'ta `/etc/bermap/appsettings.Production.json`; ardından servisi yeniden başlatın). Yükseltmeler bu ayarı korur.
- Kasa dizini (`vault`) ana anahtarı, token imzalama anahtarını ve şifreli kimlik bilgilerini içerir; yedekleyin ve erişimini kısıtlı tutun. Kasa kaybolursa kayıtlı entegrasyon parolaları yeniden girilmelidir.
- Paketler kod imzalı değildir; `SHA256SUMS.txt` ile bütünlüğü doğrulayın.
