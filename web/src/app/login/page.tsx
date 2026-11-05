'use client';

import { useState, useEffect, Suspense } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { motion } from 'framer-motion';
import { Eye, EyeOff, Shield, LogIn, Activity, Users, BarChart3, Lock, Zap, Globe } from 'lucide-react';
import { APP_NAME, APP_SHORT_NAME } from '@/config';
import { useAuth } from '@/lib/auth';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { toast } from 'sonner';
import DownloadAppSection from '@/components/DownloadAppSection';

const floatingIcons = [
  { icon: Activity, x: '10%', y: '15%', delay: 0, size: 20 },
  { icon: Users, x: '80%', y: '20%', delay: 0.5, size: 24 },
  { icon: BarChart3, x: '15%', y: '75%', delay: 1, size: 22 },
  { icon: Lock, x: '75%', y: '80%', delay: 1.5, size: 18 },
  { icon: Zap, x: '85%', y: '50%', delay: 0.3, size: 20 },
  { icon: Globe, x: '25%', y: '45%', delay: 0.8, size: 16 },
];

const stats = [
  { label: 'Active Users', value: '10K+' },
  { label: 'Data Points', value: '50M+' },
  { label: 'Uptime', value: '99.9%' },
];

// Inner component that uses useSearchParams — wrapped in Suspense
function LoginForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { login, isAuthenticated, isLoading: authLoading } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [isLoading, setIsLoading] = useState(false);

  const redirectTo = searchParams.get('redirect') || '/dashboard';

  // If already authenticated, redirect
  useEffect(() => {
    if (!authLoading && isAuthenticated) {
      router.replace('/dashboard');
    }
  }, [authLoading, isAuthenticated, router]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setIsLoading(true);

    try {
      const result = await login(email, password);
      if (result.success) {
        toast.success('Welcome back!', {
          description: 'You have been logged in successfully.',
        });
        router.replace(redirectTo);
      } else {
        setError(result.error || 'Login failed');
        toast.error('Login failed', {
          description: result.error || 'Please check your credentials.',
        });
      }
    } catch (err) {
      setError('Connection error. Please ensure the server is running.');
      toast.error('Connection error', {
        description: 'Could not connect to the server. Is the backend running?',
      });
    } finally {
      setIsLoading(false);
    }
  };

  // Loading state while checking auth
  if (authLoading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background">
        <div className="flex flex-col items-center gap-3">
          <motion.div
            animate={{ rotate: 360 }}
            transition={{ duration: 1, repeat: Infinity, ease: 'linear' }}
          >
            <Shield className="w-8 h-8 text-primary" />
          </motion.div>
          <p className="text-sm text-muted-foreground">Loading...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex flex-col bg-background overflow-hidden">
      {/* Download Alpha AI Tracker — top strip */}
      <div className="fixed w-full z-20 px-4 pt-4 lg:px-8 lg:pt-6">
        <DownloadAppSection compact />
      </div>

      <div className="flex flex-1 overflow-y-auto">
      {/* Left panel - Hero */}
      <div className="hidden lg:flex lg:w-[48%] relative items-center justify-center">
        <div className="absolute inset-0" style={{
          background: 'linear-gradient(135deg, hsl(262 65% 12%) 0%, hsl(262 70% 22%) 30%, hsl(262 80% 38%) 60%, hsl(280 70% 45%) 100%)',
        }} />
        <div className="absolute inset-0 opacity-[0.07]" style={{
          backgroundImage: 'radial-gradient(circle at 25% 25%, white 1px, transparent 1px), radial-gradient(circle at 75% 75%, white 1px, transparent 1px)',
          backgroundSize: '50px 50px',
        }} />
        <motion.div animate={{ scale: [1, 1.2, 1], opacity: [0.15, 0.25, 0.15] }} transition={{ duration: 8, repeat: Infinity }} className="absolute w-[500px] h-[500px] rounded-full top-[-100px] left-[-150px]" style={{ background: 'radial-gradient(circle, hsl(262 80% 60% / 0.4), transparent 70%)' }} />
        <motion.div animate={{ scale: [1.2, 1, 1.2], opacity: [0.1, 0.2, 0.1] }} transition={{ duration: 10, repeat: Infinity }} className="absolute w-[400px] h-[400px] rounded-full bottom-[-50px] right-[-100px]" style={{ background: 'radial-gradient(circle, hsl(280 80% 55% / 0.3), transparent 70%)' }} />
        {floatingIcons.map((item, i) => {
          const Icon = item.icon;
          return (
            <motion.div
              key={i}
              className="absolute"
              style={{ left: item.x, top: item.y }}
              animate={{ y: [0, -15, 0], opacity: [0.2, 0.4, 0.2] }}
              transition={{ duration: 4 + i, repeat: Infinity, delay: item.delay }}
            >
              <div className="w-10 h-10 rounded-xl bg-white/[0.06] backdrop-blur-sm border border-white/10 flex items-center justify-center">
                <Icon className="text-white/40" style={{ width: item.size, height: item.size }} />
              </div>
            </motion.div>
          );
        })}
        <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
          {[...Array(4)].map((_, i) => (
            <motion.div
              key={i}
              className="absolute rounded-full border border-white/[0.06]"
              style={{ width: `${280 + i * 140}px`, height: `${280 + i * 140}px` }}
              animate={{ rotate: i % 2 === 0 ? 360 : -360 }}
              transition={{ duration: 60 + i * 20, repeat: Infinity, ease: 'linear' }}
            />
          ))}
        </div>
        <motion.div initial={{ opacity: 0, y: 30 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 1, ease: 'easeOut' }} className="relative z-10 text-center px-12 max-w-lg">
          <motion.div
            initial={{ scale: 0 }}
            animate={{ scale: 1 }}
            transition={{ type: 'spring', stiffness: 200, delay: 0.3 }}
            className="mx-auto mb-10"
          >
            <div className="w-24 h-24 rounded-3xl bg-white flex items-center justify-center mx-auto border border-white/20 shadow-[0_0_60px_rgba(139,92,246,0.3)]">
              <img src="/app-logo.png" alt="Alpha AI Tracking logo" className="app-logo w-12 h-12" />
            </div>
          </motion.div>
          <motion.h1 initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.5 }} className="text-5xl font-display font-extrabold text-white mb-3 tracking-tight">
            {APP_SHORT_NAME.split(' ').map((word,i) => (
              <span key={i} className={i === 1 ? 'text-transparent bg-clip-text' : ''} style={i === 1 ? { backgroundImage: 'linear-gradient(135deg, hsl(262 90% 75%), hsl(280 90% 80%))' } : undefined}>{word}{i < APP_SHORT_NAME.split(' ').length-1 ? ' ' : ''}</span>
            ))}
          </motion.h1>
          <motion.h2 initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.6 }} className="text-lg font-display font-medium text-white/60 mb-8">Monitoring & Productivity System</motion.h2>
          <motion.p initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: 0.8 }} className="text-white/40 text-sm max-w-sm mx-auto leading-relaxed mb-12">Real-time workforce analytics, AI-driven productivity insights, and comprehensive monitoring — built for modern enterprises.</motion.p>
          <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 1 }} className="flex items-center justify-center gap-8">
            {stats.map((s, i) => (
              <div key={i} className="text-center">
                <p className="text-2xl font-display font-bold text-white">{s.value}</p>
                <p className="text-[11px] text-white/40 mt-1">{s.label}</p>
              </div>
            ))}
          </motion.div>
        </motion.div>
        <div className="absolute bottom-0 left-0 right-0 h-1" style={{ background: 'linear-gradient(90deg, transparent, hsl(262 80% 60%), hsl(280 80% 60%), transparent)' }} />
      </div>

      {/* Right panel - Login form */}
      <div className="flex-1 flex items-center justify-center p-6 lg:p-16 relative">
        <div className="absolute inset-0 opacity-[0.02]" style={{
          backgroundImage: 'radial-gradient(circle, hsl(262 80% 50%) 1px, transparent 1px)',
          backgroundSize: '30px 30px',
        }} />
        <motion.div initial={{ opacity: 0, x: 30 }} animate={{ opacity: 1, x: 0 }} transition={{ duration: 0.7, delay: 0.2 }} className="w-full max-w-[420px] relative z-10">
          <div className="lg:hidden flex items-center gap-3 mb-10">
            <motion.div initial={{ scale: 0 }} animate={{ scale: 1 }} transition={{ type: 'spring' }} className="w-12 h-12 rounded-2xl flex items-center justify-center shadow-lg bg-white" style={{ boxShadow: '0 8px 30px hsl(262 80% 50% / 0.3)' }}>
              <img src="/app-logo.png" alt="Alpha AI Tracking logo" className="app-logo w-6 h-6" />
            </motion.div>
            <div>
              <p className="font-display font-extrabold text-lg text-foreground">{APP_NAME}</p>
              <p className="text-xs text-muted-foreground">Tracking, Monitoring & Productivity</p>
            </div>
          </div>
          <div className="mb-8">
            <h2 className="text-3xl font-display font-extrabold text-foreground mb-2">Welcome back</h2>
            <p className="text-muted-foreground text-sm">Sign in with your company admin credentials</p>
          </div>
          {error && (
            <motion.div initial={{ opacity: 0, y: -10, scale: 0.95 }} animate={{ opacity: 1, y: 0, scale: 1 }} className="mb-5 p-4 rounded-xl bg-destructive/10 border border-destructive/20 text-destructive text-sm flex items-center gap-2">
              <span className="text-lg">⚠️</span> {error}
            </motion.div>
          )}
          <form onSubmit={handleSubmit} className="space-y-5">
            <div>
              <label className="block text-sm font-medium text-foreground mb-1.5">Email</label>
              <Input type="email" value={email} onChange={e => setEmail(e.target.value)} placeholder="admin@alphai.com" required className="w-full" />
            </div>
            <div>
              <label className="block text-sm font-medium text-foreground mb-1.5">Password</label>
              <div className="relative">
                <Input type={showPassword ? 'text' : 'password'} value={password} onChange={e => setPassword(e.target.value)} placeholder="Enter your password" required className="w-full pr-10" />
                <button type="button" onClick={() => setShowPassword(!showPassword)} className="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground transition-colors">
                  {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                </button>
              </div>
            </div>
            <Button type="submit" disabled={isLoading} className="w-full gradient-primary text-primary-foreground py-6 rounded-xl text-sm font-semibold hover:opacity-90 transition-all duration-200 shadow-lg shadow-primary/25">
              {isLoading ? (
                <motion.div animate={{ rotate: 360 }} transition={{ duration: 1, repeat: Infinity, ease: 'linear' }}>
                  <Shield className="w-5 h-5" />
                </motion.div>
              ) : (
                <><LogIn className="w-5 h-5 mr-2" /> Sign In</>
              )}
            </Button>
          </form>
          <p className="mt-8 text-center text-xs text-muted-foreground">&copy; {new Date().getFullYear()} {APP_NAME}. All rights reserved.</p>
        </motion.div>
      </div>
      </div>
    </div>
  );
}

// Main export — wraps the form in Suspense per Next.js 15 requirement
export default function LoginPage() {
  return (
    <Suspense fallback={
      <div className="min-h-screen flex items-center justify-center bg-background">
        <div className="flex flex-col items-center gap-3">
          <motion.div animate={{ rotate: 360 }} transition={{ duration: 1, repeat: Infinity, ease: 'linear' }}>
            <Shield className="w-8 h-8 text-primary" />
          </motion.div>
          <p className="text-sm text-muted-foreground">Loading...</p>
        </div>
      </div>
    }>
      <LoginForm />
    </Suspense>
  );
}
