import React from 'react';
import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { TopNav } from './TopNav';

export const AppLayout: React.FC = () => {
  return (
    <div className="flex h-screen w-screen overflow-hidden bg-neutral-100 dark:bg-dark-bg text-neutral-900 dark:text-dark-text transition-colors">
      {/* Sidebar */}
      <Sidebar />

      {/* Main Column */}
      <div className="flex flex-col flex-1 h-full min-w-0 overflow-hidden">
        {/* Top Navigation */}
        <TopNav />

        {/* Dynamic Route Content */}
        <main className="flex-1 overflow-y-auto p-6 transition-all">
          <Outlet />
        </main>
      </div>
    </div>
  );
};
