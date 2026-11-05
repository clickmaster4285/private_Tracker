import { Bell, Menu, Search, LogOut } from 'lucide-react';
import { useAuth, getRoleName } from '@/lib/auth';
import Link from 'next/link';

interface TopBarProps {
  title: string;
  onMenuClick: () => void;
}

export default function TopBar({ title, onMenuClick }: TopBarProps) {
  const { user, logout } = useAuth();

  return (
    <header className="sticky top-0 z-20 bg-card border-b border-border px-4 lg:px-6 py-3 flex items-center justify-between gap-4">
      <div className="flex items-center gap-3">
        <button onClick={onMenuClick} className="lg:hidden p-2 rounded-lg hover:bg-muted transition-colors">
          <Menu className="w-5 h-5 text-foreground" />
        </button>
        <h1 className="font-display font-bold text-lg lg:text-xl text-foreground">{title}</h1>
      </div>
      <div className="flex items-center gap-2">
        <div className="hidden md:flex items-center bg-muted rounded-lg px-3 py-2 gap-2 w-64">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input type="text" placeholder="Search..." className="bg-transparent border-none outline-none text-sm text-foreground placeholder:text-muted-foreground flex-1" />
        </div>
        <button className="relative p-2 rounded-lg hover:bg-muted transition-colors">
          <Bell className="w-5 h-5 text-muted-foreground" />
          <span className="absolute top-1.5 right-1.5 w-2 h-2 rounded-full bg-destructive" />
        </button>
        {user && (
          <div className="flex items-center gap-2 ml-1">
            {/* <div className="group flex items-center cursor-pointer"> */}
<Link
  href="/settings/profile"
  className="group flex items-center cursor-pointer"
>
  <div className="hidden sm:flex flex-col items-end mr-1">
    <span className="text-xs font-semibold text-foreground group-hover:text-primary transition-colors">
      {user.name}
    </span>

    <span className="text-[10px] text-muted-foreground group-hover:text-primary transition-colors">
      {getRoleName(user.role)}
    </span>
  </div>

  <div
    className="w-9 h-9 rounded-full flex items-center justify-center text-white text-sm font-bold"
    style={{ backgroundColor: user.avatarColor }}
  >
    {user.avatar}
  </div>
</Link>
{/* </div> */}
            <button onClick={logout} className="p-2 rounded-lg hover:bg-destructive/10 text-muted-foreground hover:text-destructive transition-colors" title="Logout">
              <LogOut className="w-4 h-4" />
            </button>
          </div>
        )}
      </div>
    </header>
  );
}
