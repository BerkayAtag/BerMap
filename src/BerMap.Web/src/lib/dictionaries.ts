export type TranslationKey =
  | 'dashboard'
  | 'infrastructureMap'
  | 'inventory'
  | 'simulation'
  | 'berSimEngine'
  | 'snapshots'
  | 'capacityIntelligence'
  | 'reports'
  | 'integrations'
  | 'collectors'
  | 'auditLogs'
  | 'users'
  | 'enterprise'
  | 'mainNavigation'
  | 'collapseSidebar'
  | 'expandSidebar'
  | 'openNavigation'
  | 'closeNavigation'
  | 'openSearch'
  | 'closeSearch'
  | 'userMenu'
  | 'changePassword'
  | 'systemHealthNormal'
  | 'systemHealthWarning'
  | 'systemHealthCritical'
  | 'systemHealthUnknown'
  | 'searchPlaceholder'
  | 'searchLabel'
  | 'searching'
  | 'searchNoResults'
  | 'searchFailed'
  | 'logout'
  | 'restoringSession'
  | 'sessionRestoreFailed'
  | 'retry'
  | 'roleUnknown'
  | 'rolePlatformAdministrator'
  | 'roleIntegrationAdministrator'
  | 'roleInfrastructureOperator'
  | 'roleSecurityAuditor'
  | 'roleReadOnlyViewer'
  | 'lightMode'
  | 'darkMode'
  | 'switchToLight'
  | 'switchToDark'
  | 'locale'
  | 'localeMenuTitle'
  | 'changeLanguage'
  | 'localeTurkish'
  | 'localeEnglish'
  | 'statusHealthy'
  | 'statusWarning'
  | 'statusCritical'
  | 'statusUnknown'
  | 'multiSourceReconciled'
  | 'close'
  | 'noPermission';

const dictionaries: Record<'tr' | 'en', Record<TranslationKey, string>> = {
  tr: {
    dashboard: 'Dashboard',
    infrastructureMap: 'Altyapı Haritası',
    inventory: 'Envanter',
    simulation: 'Simülasyon',
    berSimEngine: 'BerSim Motoru',
    snapshots: 'Snapshot',
    capacityIntelligence: 'Kapasite İstihbaratı',
    reports: 'Raporlar',
    integrations: 'Entegrasyonlar',
    collectors: 'Collectorlar',
    auditLogs: 'Denetim Günlükleri',
    users: 'Kullanıcılar',
    enterprise: 'KURUMSAL',
    mainNavigation: 'Ana gezinme',
    collapseSidebar: 'Daralt',
    expandSidebar: 'Genişlet',
    openNavigation: 'Menüyü aç',
    closeNavigation: 'Menüyü kapat',
    openSearch: 'Aramayı aç',
    closeSearch: 'Aramayı kapat',
    userMenu: 'Kullanıcı menüsü',
    changePassword: 'Parolayı değiştir',
    systemHealthNormal: 'Sistem Sağlığı: Normal',
    systemHealthWarning: 'Sistem Sağlığı: Uyarı',
    systemHealthCritical: 'Sistem Sağlığı: Kritik',
    systemHealthUnknown: 'Sistem Sağlığı: Bilinmiyor',
    searchPlaceholder: 'Sunucu, cluster, IP veya servis ara... (⌘K)',
    searchLabel: 'Global arama',
    searching: 'Aranıyor...',
    searchNoResults: 'Sonuç bulunamadı',
    searchFailed: 'Arama başarısız oldu',
    logout: 'Çıkış Yap',
    restoringSession: 'Oturum doğrulanıyor...',
    sessionRestoreFailed: 'Sunucuya ulaşılamadı; oturumunuz korunuyor.',
    retry: 'Tekrar dene',
    roleUnknown: 'Bilinmeyen Rol',
    rolePlatformAdministrator: 'Platform Yöneticisi',
    roleIntegrationAdministrator: 'Entegrasyon Yöneticisi',
    roleInfrastructureOperator: 'Altyapı Operatörü',
    roleSecurityAuditor: 'Güvenlik Denetçisi',
    roleReadOnlyViewer: 'Salt-Okunur Görüntüleyici',
    lightMode: 'Açık Tema',
    darkMode: 'Koyu Tema',
    switchToLight: 'Açık Temaya Geç',
    switchToDark: 'Koyu Temaya Geç',
    locale: 'Dil',
    localeMenuTitle: 'Dil / Language',
    changeLanguage: 'Dili değiştir',
    localeTurkish: 'Türkçe',
    localeEnglish: 'İngilizce',
    statusHealthy: 'Sağlıklı',
    statusWarning: 'Uyarı',
    statusCritical: 'Kritik',
    statusUnknown: 'Bilinmiyor',
    multiSourceReconciled: 'Çok Kaynaklı Uzlaştırılmış',
    close: 'Kapat',
    noPermission: 'Bu işlem için yetkiniz yok.',
  },
  en: {
    dashboard: 'Dashboard',
    infrastructureMap: 'Infrastructure Map',
    inventory: 'Inventory',
    simulation: 'Simulation',
    berSimEngine: 'BerSim Engine',
    snapshots: 'Snapshots',
    capacityIntelligence: 'Capacity Intelligence',
    reports: 'Reports',
    integrations: 'Integrations',
    collectors: 'Collectors',
    auditLogs: 'Audit Logs',
    users: 'Users',
    enterprise: 'ENTERPRISE',
    mainNavigation: 'Main navigation',
    collapseSidebar: 'Collapse',
    expandSidebar: 'Expand',
    openNavigation: 'Open menu',
    closeNavigation: 'Close menu',
    openSearch: 'Open search',
    closeSearch: 'Close search',
    userMenu: 'User menu',
    changePassword: 'Change password',
    systemHealthNormal: 'System Health: Normal',
    systemHealthWarning: 'System Health: Warning',
    systemHealthCritical: 'System Health: Critical',
    systemHealthUnknown: 'System Health: Unknown',
    searchPlaceholder: 'Search server, cluster, IP or service... (⌘K)',
    searchLabel: 'Global search',
    searching: 'Searching...',
    searchNoResults: 'No results found',
    searchFailed: 'Search failed',
    logout: 'Sign Out',
    restoringSession: 'Restoring session...',
    sessionRestoreFailed: 'The server could not be reached; your session is kept.',
    retry: 'Retry',
    roleUnknown: 'Unknown Role',
    rolePlatformAdministrator: 'Platform Administrator',
    roleIntegrationAdministrator: 'Integration Administrator',
    roleInfrastructureOperator: 'Infrastructure Operator',
    roleSecurityAuditor: 'Security Auditor',
    roleReadOnlyViewer: 'Read-Only Viewer',
    lightMode: 'Light Mode',
    darkMode: 'Dark Mode',
    switchToLight: 'Switch to Light Theme',
    switchToDark: 'Switch to Dark Theme',
    locale: 'Language',
    localeMenuTitle: 'Language / Dil',
    changeLanguage: 'Change language',
    localeTurkish: 'Turkish',
    localeEnglish: 'English',
    statusHealthy: 'Healthy',
    statusWarning: 'Warning',
    statusCritical: 'Critical',
    statusUnknown: 'Unknown',
    multiSourceReconciled: 'Multi-Source Reconciled',
    close: 'Close',
    noPermission: 'You do not have permission for this action.',
  },
};

/*
 * Simulation recommendation templates, keyed by the engine's recommendation code
 * (ImpactAnalysisResult.recommendationItems[].code). {name} placeholders are
 * filled from the item's values; an unknown code falls back to the engine text.
 * headroom_sufficient_post_failure is used when clusterHeadroom.isPostFailure.
 */
export const recommendationTemplates: Record<'tr' | 'en', Record<string, string>> = {
  tr: {
    no_direct_impact:
      '{target} için doğrudan bağlı iş yükü bulunamadı; mevcut grafikte aşağı yönlü etki yarıçapı tespit edilmedi.',
    metrics_missing:
      'Barındıran cluster için kapasite metrikleri eksik; değişikliği planlamadan önce metrik toplama açık bir vCenter keşfi çalıştırın.',
    failover_not_confirmed: 'Failover kapasitesi doğrulanamadı. Değişikliği planlamadan önce yedekliliği kontrol edin.',
    headroom_sufficient: 'Cluster kapasitesi modellenen kesinti için yeterli: CPU {cpu}%, RAM {ram}% kalıyor.',
    headroom_sufficient_post_failure:
      'Cluster kapasitesi modellenen kesinti için yeterli: arızalı hostun kapasitesi düşüldükten sonra (N-1) CPU {cpu}%, RAM {ram}% kalıyor.',
    workloads_unavailable: '{target} bileşenine doğrudan bağlı {count} iş yükü arıza sürdükçe erişilemez olacak.',
    dependents_degrade:
      '{count} bağımlı servis etkilenebilir ({names}); bakım penceresinden önce bu servislerin arıza davranışını doğrulayın.',
    ha_disabled: '{cluster} üzerinde VMware HA etkin değil; VM’ler otomatik olarak yeniden başlatılmayacak.',
    single_point_of_failure:
      '{target} tek hata noktası: iş yüklerini devralabilecek başka bir cluster hostu yok.',
    graph_truncated:
      '{target} çevresindeki graf yanıt sınırını aştı; sonuç eksik olabilir. Etkiyi doğrulamak için hedefe daha yakın bir bileşenle tekrar simüle edin.',
  },
  en: {
    no_direct_impact:
      'No directly connected workloads found for {target}; no downstream blast radius detected in the current graph.',
    metrics_missing:
      'Capacity metrics are missing for the hosting cluster. Run a vCenter discovery with metrics collection enabled before scheduling the change.',
    failover_not_confirmed: 'Failover capacity is not confirmed. Verify redundancy before scheduling the change.',
    headroom_sufficient: 'Cluster headroom is sufficient for the modelled outage: CPU {cpu}%, RAM {ram}% remaining.',
    headroom_sufficient_post_failure:
      "Cluster headroom is sufficient for the modelled outage: CPU {cpu}%, RAM {ram}% remaining after the failed host's capacity is removed (N-1).",
    workloads_unavailable: '{count} workload(s) directly attached to {target} will be unavailable while the fault persists.',
    dependents_degrade:
      '{count} dependent service(s) may degrade ({names}); confirm their failure mode before the maintenance window.',
    ha_disabled: 'VMware HA is not enabled on {cluster}; there will be no automatic VM restart.',
    single_point_of_failure: '{target} is a single point of failure: no other cluster host can take over its workloads.',
    graph_truncated:
      'The graph around {target} exceeded the response limit, so this result may be incomplete. Re-run against a component closer to the target to confirm the impact.',
  },
};

export default dictionaries;
