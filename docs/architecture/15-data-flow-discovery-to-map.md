# Veri Akışı: Keşif → Uzlaştırma → Envanter → Harita/BerSim

Bu doküman, yeni bir uygulamanın/suncunun BerMap envanterine ve haritaya nasıl otomatik
girdiğini açıklar. **Manuel düğüm oluşturma yoktur**; tüm varlıklar gerçek keşif
çalışmalarından türetilir.

## 1. Keşif (Discovery)

1. `Entegrasyonlar` sayfasından Active Directory veya VMware vCenter için **salt-okunur**
   bağlantı tanımlanır (LDAPS/StartTLS zorunlu; düz LDAP yalnızca açık onayla).
2. `Keşfi Başlat`, kalıcı bir `DiscoveryJob` kaydı oluşturur ve PostgreSQL kuyruğuna yazar.
3. `DiscoveryBackgroundService` (background worker) işi alır; adaptör hedef sisteme
   salt-okunur sorgular çalıştırır (AD: forest/domain/OU/computer/group/trust;
   vCenter: datacenter/cluster/ESXi/VM/datastore/network/resource pool).
4. İş ilerledikçe `entitiesDiscovered` / `relationshipsDiscovered` sayaçları güncellenir;
   sonuçlar ham `NodeSourceLink` kayıtlarıyla birlikte graf tablolarına yazılır.

## 2. Uzlaştırma (Reconciliation)

- Yeni ham kayıtlar, kanıta dayalı eşleştirme anahtarlarıyla (BIOS UUID, VM Instance
  UUID, domain SID, FQDN, IP/MAC) mevcut envanterle karşılaştırılır.
- **Kesin eşleşme** → varlık birleştirilir, `sourceCount` ve `sourceSystems` artar.
- **Belirsiz eşleşme** → `pendingReview` işaretlenir ve Envanter sayfasındaki
  *İnceleme Gerekli* kuyruğuna düşer. BerMap kısa hostname tek başına otomatik
  birleştirme **yapmaz**; karar kullanıcıdan alınır (Birleştir / Yok Say).

## 3. Envanter & Graf

- Uzlaştırılan varlıklar `InfrastructureNode` + `InfrastructureRelationship`
  grafiğini oluşturur. Her düğümde `firstSeen/lastSeen`, `isStale`, keşif kaynakları
  ve kanıt bağlantıları bulunur.

## 4. Harita ve BerSim'in Güncellenmesi

- `/map` sayfası `GET /api/graph/topology?depth=3` uç noktasından grafı çeker ve
  60 saniyede bir yeniler (kullanıcı da elle yenileyebilir). Yeni keşfedilen her
  sunucu/uygulama, uzlaştırma tamamlandığı anda haritada otomatik görünür.
- `/impact` ve `/simulation` (BerSim) hedef listesini yine aynı graf uç noktasından
  alır; `POST /api/simulations/run` yalnızca gerçek envanter verisi üzerinde
  deterministik etki hesabı yapar. Snapshot seçilirse hesap donmuş sürüm üzerinden
  koşar.
- **Envantere yeni uygulama/sunucu ekleme:** yalnızca (a) yeni bir keşif çalışması,
  (b) varlık üzerinde gerçekleşen sonraki keşif döngüsü, veya (c) uzlaştırma
  incelemesinin birleştirme kararı ile mümkündür. UI'da düğüm ekleme/silme formu
  bilinçli olarak bulunmaz.

## Sağlama (Doğrulama durumu)

- PostgreSQL bağlantısı olmadan (yerel ortamda `28P01`) uçtan uca çalıştırma
  doğrulanmış **değildir**; harita/BerSim'in gerçek veriyle dolduğu görüntüsü
  canlı ortamda teyit bekler.
- Gerçek AD/vCenter keşifleri hava boşluklu (air-gapped) hedeflerle canlı
  doğrulama bekler.
