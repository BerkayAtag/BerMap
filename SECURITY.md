# Güvenlik

Gerçek parolaları, kurum envanter verilerini, `.bermap-vault` içeriğini (ana anahtar, token imzalama anahtarı, şifreli kimlik bilgileri) ve sertifika özel anahtarlarını depoya veya issue'lara eklemeyin. Güvenlik bulgularını kurumunuzun iç güvenlik kanalına iletin.

## Bu sürümde olanlar

- Konsol girişi yerel hesaplarla yapılır; parolalar PBKDF2-SHA256 (210.000 tur) ile saklanır ve en az 12 karakter olmalı, kullanıcı adını içermemelidir. Oturum HMAC-SHA256 imzalı ve süreli bir token'dır (`BerMap:Auth:TokenLifetimeMinutes`, varsayılan 480 dakika). Giriş uç noktası IP başına dakikada 10 denemeyle sınırlıdır.
- Oturum iptali: her token hesabın güvenlik damgasını taşır. Parola değişikliği, yönetici tarafından parola sıfırlama, hesabın devre dışı bırakılması ve rol değişikliği damgayı yeniler; o ana kadar verilmiş tüm token'lar geçersiz olur. Değişikliği yapan sunucu örneğinde etki hemendir, en geç 30 saniye içinde (kullanıcı durumu önbelleği) her istekte geçerlidir. Yetkilendirme token'daki rolü değil, kullanıcının o anki rolünü kullanır.
- Çıkış yalnız tarayıcıdaki token'ı siler; sunucu tarafında bir çıkış uç noktası yoktur. Ele geçirilmiş bir token süresi dolana veya kullanıcının damgası yenilenene kadar geçerlidir: şüpheli durumda kullanıcının parolasını sıfırlayın veya hesabı devre dışı bırakın (imzalama anahtarını döndürmek gerekmez).
- Login, sağlık uçları ve collector protokolü dışındaki tüm `/api` uçları kimlik doğrulaması ister. Entegrasyon değişiklikleri `PlatformAdministrator` / `IntegrationAdministrator`, snapshot ve inceleme kararları ayrıca `InfrastructureOperator` rolü gerektirir. Kullanıcı yönetimi yalnız `PlatformAdministrator`, denetim kaydını okumak `PlatformAdministrator` / `SecurityAuditor` içindir. Son etkin platform yöneticisi devre dışı bırakılamaz veya rolü düşürülemez.
- Collector uçları (`register`, `heartbeat`, `submit-batch`) `X-BerMap-Enrollment-Token` başlığını sabit süreli karşılaştırmayla doğrular. Token yapılandırılmadıysa collector kaydı kapalıdır.
- Entegrasyon kimlik bilgileri AES-256-GCM ile şifrelenir; şifreli metin anahtar adına bağlıdır. API hiçbir zaman kimlik bilgisini döndürmez. Kasa dosyaları Unix'te yalnız servis hesabının okuyabileceği izinlerle (0600) ve atomik olarak yazılır. Varsayılan kasa dizini uygulama klasörünün dışındadır (`%ProgramData%\BerMap\vault`, `/var/lib/bermap/vault`).
- Kayıtlı bir kimlik bilgisi yalnız girildiği uç noktaya gönderilir: kimlik bilgisi kayıtlı bir entegrasyonun sunucu, port, kullanıcı adı veya TLS/düz metin ayarlarını değiştirmek için kimlik bilgisini yeniden girmek (ya da kayıtlıyı silmek) gerekir; aksi halde istek `400 SECRET_REQUIRED_FOR_ENDPOINT_CHANGE` ile reddedilir.
- Denetim kaydındaki kullanıcı yalnız doğrulanmış kimlikten alınır; istemci başlıklarına güvenilmez. `X-Forwarded-For` / `X-Forwarded-Proto` yalnız `BerMap:ReverseProxy:KnownProxies` listesindeki vekillerden kabul edilir.
- Adaptörler salt okunurdur. LDAPS ve vCenter TLS sertifika doğrulamasını kapatmayın.

## Bu sürümde olmayanlar

MFA, AD/LDAP ile konsol girişi, sunucu tarafında çıkış (tek bir token'ı iptal etme), collector başına kimlik (mTLS) ve WORM denetim kaydı yoktur. Kasa ana anahtarı DPAPI veya bir KMS ile sarılmaz; kasa dizininin işletim sistemi izinleriyle korunması gerekir.
