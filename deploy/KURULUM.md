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

Betik veritabanı parolasını sorar, dosyaları `C:\Program Files\BerMap\Server` altına kopyalar, `appsettings.Production.json` dosyasını yazar (yalnız SYSTEM ve Administrators okuyabilir), **BerMapServer** Windows servisini kaydeder, güvenlik duvarında portu açar ve sunucu hazır olana kadar bekler.

| Parametre | Varsayılan | Açıklama |
|---|---|---|
| `-Port` | `8080` | Dinlenecek port |
| `-CertificatePath`, `-CertificatePassword` | — | `.pfx` verilirse konsol HTTPS ile sunulur |
| `-DbHost`, `-DbPort`, `-DbName`, `-DbUser`, `-DbPassword` | `5432`, `bermap` | PostgreSQL bağlantısı |
| `-InstallDir`, `-DataDir` | `Program Files\BerMap\Server`, `ProgramData\BerMap` | Program ve kasa dizinleri |

**Yükseltme:** yeni paketi açıp betiği parametresiz yeniden çalıştırın. Yapılandırma, kasa ve veritabanı korunur.
**Kaldırma:** `.\Uninstall-BerMapServer.ps1` (kasa ve veritabanı korunur; kasayı da silmek için `-RemoveData`).

## Linux (systemd)

```bash
tar xzf bermap-server-<sürüm>-linux-x64.tar.gz
cd bermap-server-<sürüm>-linux-x64
sudo ./install.sh --db-host localhost --db-user bermap
```

Program `/opt/bermap/server`, yapılandırma `/etc/bermap/appsettings.Production.json`, kasa `/var/lib/bermap/vault` altına kurulur; servis `bermap-server` adıyla `bermap` kullanıcısıyla çalışır. Parolayı sormadan vermek için `BERMAP_DB_PASSWORD` ortam değişkenini kullanın. Günlükler: `journalctl -u bermap-server`.

**Yükseltme:** yeni paketle `sudo ./install.sh` (parametresiz). **Kaldırma:** `sudo ./uninstall.sh` (`--remove-data` kasayı ve yapılandırmayı da siler).

## İlk giriş

Kurulum sonunda betik konsol adresini, ilk yönetici (`admin`) parolasını ve collector kayıt token'ını yazar. Parola kasa dizinindeki `initial-admin-password.txt` dosyasındadır; ilk girişten sonra **Kullanıcılar** ekranından yeni parola belirleyip bu dosyayı silin.

## Güvenlik notları

- Konsol varsayılan olarak düz HTTP ile sunulur. Üretimde `-CertificatePath` ile HTTPS kullanın ya da IIS/nginx gibi TLS sonlandıran bir ters vekilin arkasına alın. Ters vekil kullanıyorsanız adresini `BerMap:ReverseProxy:KnownProxies` ayarına ekleyin.
- Kasa dizini (`vault`) ana anahtarı, token imzalama anahtarını ve şifreli kimlik bilgilerini içerir; yedekleyin ve erişimini kısıtlı tutun. Kasa kaybolursa kayıtlı entegrasyon parolaları yeniden girilmelidir.
- Paketler kod imzalı değildir; `SHA256SUMS.txt` ile bütünlüğü doğrulayın.
