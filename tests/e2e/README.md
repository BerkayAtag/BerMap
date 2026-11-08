# Uçtan uca tarayıcı testleri

Web konsolunun her işlevini gerçek bir BerMap sunucusu ve PostgreSQL'e karşı [Playwright](https://playwright.dev) ile dener. CI'da her push ve pull request'te `e2e` işi olarak çalışır ([ci.yml](../../.github/workflows/ci.yml)): release paketinin aynısı (`deploy/package.sh … linux-x64`) boş bir veritabanıyla başlatılır ve konsol, sunucunun kendisinden (aynı origin) açılır.

## Ne test edilir

| Dosya | Kapsam |
|---|---|
| `auth.spec.ts` | Hatalı/doğru giriş, İngilizce arayüzde yerelleştirilmiş giriş hatası, korumalı rota yönlendirmesi (sorgu dizesi korunur), geçersiz token ("oturum sona erdi" notu), çıkış |
| `pages.spec.ts` | Her sayfa masaüstünde ve 390 px'te, açık ve koyu temada: konsol hatası, başarısız `/api` isteği ve yatay taşma yok; menü, kenar çubuğu, tema/dil, global arama, mobil çekmece |
| `roles.spec.ts` | Salt-Okunur Görüntüleyici, Altyapı Operatörü, Güvenlik Denetçisi ve Platform Yöneticisi için menü öğeleri ve düğmeler |
| `users.spec.ts` | Kullanıcı oluşturma, rol düzenleme, parola sıfırlama, devre dışı bırakma (oturum sonlanır), istemci doğrulaması, kullanıcı menüsünden kendi parolasını değiştirme (oturum açık kalır), yöneticinin kendi rolünü değiştirmesi ve kendi parolasını sıfırlaması (önceden uyarı, ardından nedenini söyleyen giriş sayfası) |
| `integrations.spec.ts` | Sihirbazla VMware entegrasyonu oluşturma, düzenleme (API'den eklenen ayar anahtarları korunur), devre dışı bırakma/etkinleştirme, silme; collector yönetimli kartlar; düz HTTP'de kimlik kopyalama ve tam kimliğin seçili gösterilmesi; AD sihirbazında LDAPS/StartTLS/düz LDAP port eşlemesi; uç nokta değişince parolanın yeniden istenmesi (istemci ve sunucu `SECRET_REQUIRED_FOR_ENDPOINT_CHANGE`) |
| `collectors.spec.ts` | Collector kartı ve metrikleri, kayıt bilgisi, onaylı kaldırma |
| `inventory.spec.ts` | Arama, tür filtresi, `?q=` ve `?node=` derin bağlantıları, bulunamayan düğüm, CSV dışa aktarımının BOM ve formül enjeksiyonu koruması |
| `map.spec.ts` | Filtre sayacı (beklenen sayılar sayfanın kendi topoloji yanıtından), eşleşme yok bildirimi, düğüm panelinden envantere ve simülasyona geçiş |
| `dialogs.spec.ts` | Her modal iletişim kutusunda klavye odağı: açılınca içeri taşınır, Tab/Shift+Tab içeride kalır, kapanınca açan denetime döner |
| `simulation.spec.ts` | `?target=` ile host kesintisi (N-1), listeden iş yükü kesintisi, Markdown risk raporu içeriği, yazdırma görünümü, görüntüleyici rolüyle çalıştırma |
| `snapshots.spec.ts` | `?id=` detayı, bütünlük doğrulaması, oluşturma ve iki snapshot'ı karşılaştırma |
| `audit-reports.spec.ts` | Denetimde işleme göre süzme ve süzülmüş CSV; Raporlar sayfasındaki tüm dışa aktarımlar |
| `capacity.spec.ts` | %80 uyarı / %90 kritik eşikleri, demo cluster ve host değerleri, envantere geçiş |

`global-setup.ts` sunucu hazır olana kadar bekler, sentetik demo verisini gerçek API'den yükler (`support/seed.ts`: collector yönetimli iki entegrasyon, `final=true` collector gönderimleriyle 37 düğüm, bir collector, bir snapshot) ve her rol için bir hesap açar. Rol oturumları `.auth/` altına Playwright storage state olarak yazılır; testler giriş formundan geçmeden oturum açmış başlar (giriş ucu dakikada 10 denemeyle sınırlıdır). Hesapları değiştiren testler kendi kullanıcılarını oluşturur. Bazı testler kendi collector yönetimli entegrasyonunu ekler (`E2E · …`); demo verisinin sayıları bu yüzden yalnız demo düğümleri üzerinden ya da (haritadaki gibi) sayfanın o an gösterdiği API yanıtı üzerinden doğrulanır.

Tüm adlar kurgusaldır (`corp.bermap.demo`). Aynı veritabanına karşı tekrar çalıştırmak güvenlidir.

## Yerelde çalıştırma

Gerekenler: Node.js 22+, çalışan bir BerMap sunucusu ve web konsolu (aynı origin).

```bash
# Ayrı bir veritabanı
docker run -d --name bermap-e2e-postgres -p 127.0.0.1:55440:5432 \
  -e POSTGRES_USER=bermap_e2e -e POSTGRES_PASSWORD=bermap_e2e -e POSTGRES_DB=bermap_e2e postgres:17-alpine

# Sunucu
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=55440;Database=bermap_e2e;Username=bermap_e2e;Password=bermap_e2e" \
BerMap__CredentialVault__Directory="$(mktemp -d)" \
BerMap__Bootstrap__AdminPassword='E2e-Admin-Pass-2026!' \
BerMap__Collectors__EnrollmentToken=e2e-enrollment-token-0123456789abcdef0123456789abcdef \
dotnet run --project src/BerMap.Api -- --urls http://localhost:5430

# Konsol (/api isteklerini sunucuya yönlendirir)
cd src/BerMap.Web && BERMAP_API_URL=http://localhost:5430 npx vite --port 3130

# Testler
cd tests/e2e && npm ci
BERMAP_E2E_BASE_URL=http://localhost:3130 \
BERMAP_E2E_ADMIN_PASSWORD='E2e-Admin-Pass-2026!' \
BERMAP_E2E_ENROLLMENT_TOKEN=e2e-enrollment-token-0123456789abcdef0123456789abcdef \
PW_CHROME_PATH="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
npm test
```

| Değişken | Açıklama |
|---|---|
| `BERMAP_E2E_BASE_URL` | Konsol ve API'nin ortak adresi (varsayılan `http://localhost:3130`) |
| `BERMAP_E2E_ADMIN_PASSWORD` | `admin` hesabının parolası (`BerMap:Bootstrap:AdminPassword`) |
| `BERMAP_E2E_ENROLLMENT_TOKEN` | Collector kayıt token'ı (`BerMap:Collectors:EnrollmentToken`) |
| `PW_CHROME_PATH` | Kurulu Google Chrome'u kullanır; verilmezse Playwright'ın Chromium'u gerekir (`npx playwright install chromium`) |

Paketlenmiş sunucuya karşı çalıştırmak da aynıdır: `BERMAP_E2E_BASE_URL` sunucunun adresini göstermelidir. Sonuç raporu: `npm run report`; hata ayıklama için `npx playwright test --ui` veya `--headed`. Tür denetimi: `npm run typecheck`.

## Test yazarken

- Seçicilerde rol ve erişilebilir ad kullanın (`getByRole('button', { name: 'Kaydet' })`), CSS sınıfı değil. Türkçe büyük/küçük harf eşleşmesi sürpriz yapabilir (`getByLabel('Rol')` "Parola"yı da bulur): gerektiğinde `exact: true` ya da rol kullanın.
- Bekleme için `waitForTimeout` yerine `expect(...).toBeVisible()` gibi beklemeli doğrulamalar kullanın.
- Başka testlerin verisine güvenmeyin: oluşturduğunuz kayıtlara `uniqueName()` ile benzersiz ad verin, sayıları API'den hesaplayın.
- `problems.expectNone()` sayfanın konsol hatası ve başarısız `/api` isteği üretmediğini doğrular.
- Giriş ve parola değiştirme uçları birlikte dakikada 10 istekle sınırlıdır ve `globalSetup` ile mevcut testler bu payın çoğunu kullanır. Yeni oturum gerekiyorsa `login()` yardımcısını (429'da bir sonraki pencereyi bekler) ve `test.setTimeout(150_000)` kullanın.
