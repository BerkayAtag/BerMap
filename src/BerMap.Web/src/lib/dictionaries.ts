export type TranslationKey =
  | 'dashboard'
  | 'infrastructureMap'
  | 'inventory'
  | 'impactAnalysis'
  | 'berSim'
  | 'berSimBadge'
  | 'berSimEngine'
  | 'snapshots'
  | 'capacityIntelligence'
  | 'eventsAlerts'
  | 'reports'
  | 'integrations'
  | 'collectors'
  | 'auditLogs'
  | 'enterprise'
  | 'mainNavigation'
  | 'collapseSidebar'
  | 'expandSidebar'
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
    impactAnalysis: 'Etki Analizi',
    berSim: 'BerSim',
    berSimBadge: 'Sim',
    berSimEngine: 'BerSim Motoru',
    snapshots: 'Snapshot',
    capacityIntelligence: 'Kapasite İstihbaratı',
    eventsAlerts: 'Olaylar & Uyarılar',
    reports: 'Raporlar',
    integrations: 'Entegrasyonlar',
    collectors: 'Collectorlar',
    auditLogs: 'Denetim Günlükleri',
    enterprise: 'KURUMSAL',
    mainNavigation: 'Ana gezinme',
    collapseSidebar: 'Daralt',
    expandSidebar: 'Genişlet',
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
    impactAnalysis: 'Impact Analysis',
    berSim: 'BerSim',
    berSimBadge: 'Sim',
    berSimEngine: 'BerSim Engine',
    snapshots: 'Snapshots',
    capacityIntelligence: 'Capacity Intelligence',
    eventsAlerts: 'Events & Alerts',
    reports: 'Reports',
    integrations: 'Integrations',
    collectors: 'Collectors',
    auditLogs: 'Audit Logs',
    enterprise: 'ENTERPRISE',
    mainNavigation: 'Main navigation',
    collapseSidebar: 'Collapse',
    expandSidebar: 'Expand',
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

export default dictionaries;
