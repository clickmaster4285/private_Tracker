'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Shield, Eye, EyeOff, CheckCircle, Lock } from 'lucide-react';
import { APP_NAME } from '@/config';
import Link from 'next/link';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';

export default function ResetPassword() {
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState('');

  const validations = [
    { label: 'Min 8 characters', met: password.length >= 8 },
    { label: '1 uppercase letter', met: /[A-Z]/.test(password) },
    { label: '1 special character', met: /[!@#$%^&*(),.?":{}|<>]/.test(password) },
    { label: 'Passwords match', met: password.length > 0 && password === confirm },
  ];

  const allValid = validations.every(v => v.met);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!allValid) { setError('Please meet all password requirements'); return; }
    setDone(true);
  };

  if (done) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background p-6">
        <motion.div initial={{ opacity: 0, scale: 0.95 }} animate={{ opacity: 1, scale: 1 }} className="text-center max-w-sm">
          <div className="w-16 h-16 rounded-full bg-success/15 flex items-center justify-center mx-auto mb-4">
            <CheckCircle className="w-8 h-8 text-success" />
          </div>
          <h2 className="text-xl font-display font-bold text-foreground mb-2">Password Reset!</h2>
          <p className="text-sm text-muted-foreground mb-6">Your password has been successfully reset.</p>
          <Link href="/login">
            <Button className="gradient-primary text-primary-foreground rounded-xl">Back to Login</Button>
          </Link>
        </motion.div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-background p-6">
      <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} className="w-full max-w-[420px]">
        <div className="flex items-center gap-3 mb-8">
          <div className="w-12 h-12 rounded-2xl gradient-primary flex items-center justify-center">
            <Shield className="w-6 h-6 text-primary-foreground" />
          </div>
          <div>
            <p className="font-display font-extrabold text-lg text-foreground">{APP_NAME}</p>
            <p className="text-xs text-muted-foreground">Reset Password</p>
          </div>
        </div>

        <h2 className="text-2xl font-display font-extrabold text-foreground mb-2">Create new password</h2>
        <p className="text-muted-foreground text-sm mb-6">Your new password must meet the complexity requirements below.</p>

        {error && <div className="mb-4 p-3 rounded-xl bg-destructive/10 border border-destructive/20 text-destructive text-sm">{error}</div>}

        <form onSubmit={handleSubmit} className="space-y-5">
          <div>
            <label className="text-sm font-semibold text-foreground mb-2 block">New Password</label>
            <div className="relative">
              <Input type={showPassword ? 'text' : 'password'} value={password} onChange={e => setPassword(e.target.value)} placeholder="Enter new password" className="h-12 pr-11 rounded-xl" required />
              <button type="button" onClick={() => setShowPassword(!showPassword)} className="absolute right-3.5 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground p-1">
                {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
              </button>
            </div>
          </div>
          <div>
            <label className="text-sm font-semibold text-foreground mb-2 block">Confirm Password</label>
            <Input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} placeholder="Confirm new password" className="h-12 rounded-xl" required />
          </div>

          <div className="space-y-2 p-3 bg-muted rounded-xl">
            {validations.map(v => (
              <div key={v.label} className="flex items-center gap-2 text-xs">
                <div className={`w-4 h-4 rounded-full flex items-center justify-center ${v.met ? 'bg-success text-success-foreground' : 'bg-muted-foreground/20'}`}>
                  {v.met && <CheckCircle className="w-3 h-3" />}
                </div>
                <span className={v.met ? 'text-foreground' : 'text-muted-foreground'}>{v.label}</span>
              </div>
            ))}
          </div>

          <Button type="submit" disabled={!allValid} className="w-full h-12 rounded-xl font-bold gradient-primary text-primary-foreground">
            <Lock className="w-4 h-4 mr-2" /> Reset Password
          </Button>
        </form>
      </motion.div>
    </div>
  );
}
