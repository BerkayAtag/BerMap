# Sürüm geçmişi

## 0.2.0 — 2026-11-04

### Yeni
- **Zamanlanmış keşif:** Entegrasyonların cron takvimi artık çalıştırılıyor. Beş alanlı cron (`*/15`, aralık, liste, `MON-FRI`, `@daily`) sunucunun yerel saatinde değerlendirilir (`BerMap:DiscoveryScheduler:TimeZone` ile değiştirilebilir).
  - Kesinti sonrası kaçırılan çalışmalardan yalnız biri yapılır; elle başlatılan keşif o dilimin çalışması sayılır.
  - Birden çok API sunucusu aynı veritabanını paylaşabilir; PostgreSQL advisory kilidi zamanlamanın tek sunucuda yapılmasını sağlar.
  - Geçersiz cron artık kayıtta reddediliyor (`SCHEDULE_INVALID`, nedeniyle birlikte); daha önce yalnız alan sayısı kontrol ediliyordu.
  - Entegrasyon kartında "Sonraki Keşif" zamanı gösteriliyor; sihirbazda cron örnekleri var.

## 0.1.0 — 2026-11-04 (önizleme)

İlk kamuya açık önizleme. Başka bir araçla üretilen kod tabanı baştan sona incelendi; aşağıdaki hatalar düzeltildi.

### Güvenlik
- Giriş her kullanıcı adı/parolayı kabul ediyordu ve API'de hiç yetki denetimi yoktu. Yerel hesaplar (PBKDF2-SHA256), imzalı süreli token, tüm `/api` uçlarında zorunlu kimlik doğrulama, rol politikaları ve giriş hız sınırı eklendi.
- Collector uçları (`register`, `heartbeat`, `submit-batch`) kimliksizdi; herkes envantere düğüm ekleyip gerçek düğümleri "eskimiş" yapabiliyordu. Artık hepsi kayıt token'ını sabit süreli karşılaştırmayla doğruluyor; kayıt isteği varlık yerine DTO ile alınıyor.
- Denetim kaydındaki kullanıcı `X-BerMap-Actor` başlığından ve istek gövdesinden alınabiliyordu; artık yalnız doğrulanmış kimlikten alınıyor.
- Kimlik bilgisi kasası: dosyalar 0600 izinle ve atomik yazılıyor, şifreli metin anahtar adına bağlı, bozuk ana anahtar açıkça reddediliyor, varsayılan dizin uygulama klasörünün dışında (yeniden dağıtımda şifreler kaybolmuyor).

### Veritabanı ve işletim
- Migration'lar hiç uygulanmıyordu; temiz kurulumda her uç 500 dönüyor, `/healthz/ready` yine de "Healthy" diyordu. Şema ilk açılışta kuruluyor; hazır olma kontrolü bekleyen migration'ı yakalıyor.
- Yeniden başlatmada "Running" kalan keşif işleri entegrasyonu sonsuza dek kilitliyordu; açılışta kapatılıyor.
- Üretim ayarlarında collector token anahtarı yanlış bölümdeydi (`Collector` / `Collectors`).
- Bilinmeyen `/api` yolları SPA sayfasını döndürüyordu; artık 404.

### Keşif ve uzlaştırma
- Başarısız ya da yarım kalan bir keşif, entegrasyonun tüm envanterini "eskimiş" işaretliyordu. Eskime yalnız eksiksiz ve başarılı bir görüntüden sonra uygulanıyor; çok parçalı collector gönderimleri `final` ile tamamlanıyor.
- AD adaptörü başarısız aşamaları uyarı sayıp "Completed" dönüyordu; artık hata ve `PartialSuccess`/`Failed`.
- Zaman aşımları "iptal" olarak kaydediliyordu; artık hata. İşin toplam süresi entegrasyonun zaman aşımıyla sınırlı.
- İki entegrasyon aynı düğümlere uzlaştığında benzersiz ilişki anahtarı tüm keşfi düşürüyordu.
- Collector üzerinden beslenen entegrasyonlar hep "Inactive" görünüyordu.
- VMware: SOAP quickStats hiç ayrıştırılmıyordu, eşzamanlı yeniden oturum açma oturum sızdırıyordu, 4000+ VM'de liste başarısız oluyordu, konuk kimliği yanlış uçtan okunuyordu.
- AD: Linux/macOS'ta sertifika geri çağrısı her bağlantıyı düşürüyordu; SID/GUID bazen metin olarak okunuyordu; alt OU'lar yanlış üst OU'ya bağlanabiliyordu.
- Collector: hiç kayıt olmuyordu (sabit ID), çevrimdışı kuyruk bozuk bir dosyada kalıcı olarak takılıyordu ve gönderilmeden siliyordu, CPU/bellek ölçümleri yanlıştı.

### Graf ve simülasyon
- Düğüm özellikleri API'de hep boş dönüyordu; simülasyon bu yüzden cluster kapasitesini hiç göremiyordu.
- Etki analizi bir VM hedeflendiğinde onun host'unu ve datastore'unu "etkilenmiş" sayıyordu; artık VM'e bağımlı uygulamaları buluyor.
- Bir VM düştüğünde bağlı olduğu ağ etkilenmiş sayılıyordu; bağımlılık yolları ters yönde yazılıyordu.
- Topoloji kırpması hedef düğümü düşürebiliyordu; merkezsiz istek yalnız 100 düğüm döndürüyordu.

### Web konsolu
- Giriş korumalı rotalar, oturum geri yükleme, 401'de çıkış, gerçek kullanıcı bilgisi ve çıkış düğmesi.
- Entegrasyon sihirbazında başlıklar kod olarak görünüyordu; VMware için LDAP seçenekleri gösteriliyordu; keşif başlatılamazsa ikinci kayıt oluşabiliyordu.
- İş iptali her zaman hata gösteriyordu (boş 202 yanıtı).
- CSV dışa aktarma 200 satırda kesiliyordu.
- Simülasyondaki snapshot seçici hiçbir şey yapmıyordu (kaldırıldı).
- HA/DRS hep "—" görünüyordu; harita filtresi çalışmıyordu, yerleşim okunaksızdı ve sürüklenen konumlar her yenilemede sıfırlanıyordu.
- Tema token'ları, yerelleştirme, erişilebilirlik (etiketler, iletişim kutuları, iç içe bağlantı/düğme) ve ESLint yapılandırması.

### Testler ve CI
- Yeni birim testleri (kimlik doğrulama, ingestion, simülasyon, collector kuyruğu ve istemcisi) ve PostgreSQL'e karşı uçtan uca API testleri.
- GitHub Actions: derleme, testler (PostgreSQL servisiyle), lint ve frontend derlemesi; tanıtım sayfası GitHub Pages'e yayımlanır.

### Bilinen sınırlamalar
- Zamanlanmış (cron) keşif kaydediliyor ama çalıştırılmıyor; keşif konsoldan başlatılır. (0.2.0'da giderildi.)
- Simülasyon her zaman canlı graf üzerinde çalışır.
- Collector kayıt olur, heartbeat gönderir ve kuyruğu boşaltır; adaptörleri kendisi henüz çalıştırmaz.
