'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Shield, ArrowLeft, Mail, CheckCircle } from 'lucide-react';
import { APP_NAME } from '@/config';
import Link from 'next/link';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';

export default function ForgotPassword() {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (email) setSent(true);
  };

  return (
    <div className="min-h-screen flex items-center justify-center bg-background p-6">
      <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} className="w-full max-w-[420px]">
        <div className="flex items-center gap-3 mb-8">
          <div className="w-12 h-12 rounded-2xl gradient-primary flex items-center justify-center">
            <Shield className="w-6 h-6 text-primary-foreground" />
          </div>
          <div>
            <p className="font-display font-extrabold text-lg text-foreground">{APP_NAME}</p>
            <p className="text-xs text-muted-foreground">Password Recovery</p>
          </div>
        </div>

        {!sent ? (
          <>
            <h2 className="text-2xl font-display font-extrabold text-foreground mb-2">Forgot your password?</h2>
            <p className="text-muted-foreground text-sm mb-6">Enter your registered email and we'll send you a reset link valid for 60 minutes.</p>

            <form onSubmit={handleSubmit} className="space-y-5">
              <div>
                <label className="text-sm font-semibold text-foreground mb-2 block">Email Address</label>
                <Input
                  type="email"
                  placeholder="you@company.com"
                  value={email}
                  onChange={e => setEmail(e.target.value)}
                  required
                  className="h-12 rounded-xl"
                />
              </div>
              <Button type="submit" className="w-full h-12 rounded-xl font-bold gradient-primary text-primary-foreground">
                <Mail className="w-4 h-4 mr-2" /> Send Reset Link
              </Button>
            </form>
          </>
        ) : (
          <motion.div initial={{ opacity: 0, scale: 0.95 }} animate={{ opacity: 1, scale: 1 }} className="text-center">
            <div className="w-16 h-16 rounded-full bg-success/15 flex items-center justify-center mx-auto mb-4">
              <CheckCircle className="w-8 h-8 text-success" />
            </div>
            <h2 className="text-xl font-display font-bold text-foreground mb-2">Reset Link Sent!</h2>
            <p className="text-sm text-muted-foreground mb-6">
              We've sent a password reset link to <span className="font-semibold text-foreground">{email}</span>. 
              The link is valid for 60 minutes.
            </p>
          </motion.div>
        )}

        <Link href="/login" className="flex items-center gap-2 text-sm text-primary hover:text-primary/80 mt-6 font-medium">
          <ArrowLeft className="w-4 h-4" /> Back to Login
        </Link>
      </motion.div>
    </div>
  );
}
