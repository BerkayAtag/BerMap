# Güvenlik

Gerçek parolaları, kurum envanter verilerini, `.bermap-vault` içeriğini (ana anahtar, token imzalama anahtarı, şifreli kimlik bilgileri) ve sertifika özel anahtarlarını depoya veya issue'lara eklemeyin. Güvenlik bulgularını kurumunuzun iç güvenlik kanalına iletin.

## Bu sürümde olanlar

- Konsol girişi yerel hesaplarla yapılır; parolalar PBKDF2-SHA256 (210.000 tur) ile saklanır, oturum HMAC-SHA256 imzalı ve süreli bir token'dır. Giriş uç noktası IP başına dakikada 10 denemeyle sınırlıdır.
- Login, sağlık uçları ve collector protokolü dışındaki tüm `/api` uçları kimlik doğrulaması ister. Entegrasyon değişiklikleri `PlatformAdministrator` / `IntegrationAdministrator`, snapshot ve inceleme kararları ayrıca `InfrastructureOperator` rolü gerektirir.
- Collector uçları (`register`, `heartbeat`, `submit-batch`) `X-BerMap-Enrollment-Token` başlığını sabit süreli karşılaştırmayla doğrular. Token yapılandırılmadıysa collector kaydı kapalıdır.
- Entegrasyon kimlik bilgileri AES-256-GCM ile şifrelenir; şifreli metin anahtar adına bağlıdır. Kasa dosyaları Unix'te yalnız servis hesabının okuyabileceği izinlerle (0600) ve atomik olarak yazılır. Varsayılan kasa dizini uygulama klasörünün dışındadır (`%ProgramData%\BerMap\vault`, `/var/lib/bermap/vault`).
- Denetim kaydındaki kullanıcı yalnız doğrulanmış kimlikten alınır; istemci başlıklarına güvenilmez.
- Adaptörler salt okunurdur. LDAPS ve vCenter TLS sertifika doğrulamasını kapatmayın.

## Bu sürümde olmayanlar

MFA, AD/LDAP ile konsol girişi, oturum iptali (token süresi dolana kadar geçerlidir), collector başına kimlik (mTLS) ve WORM denetim kaydı yoktur. Kasa ana anahtarı DPAPI veya bir KMS ile sarılmaz; kasa dizininin işletim sistemi izinleriyle korunması gerekir.
