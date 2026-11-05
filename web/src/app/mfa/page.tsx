'use client';

import { useState, useEffect } from 'react';
import { motion } from 'framer-motion';
import { Shield, Smartphone, Mail, MessageSquare } from 'lucide-react';
import { APP_NAME } from '@/config';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { InputOTP, InputOTPGroup, InputOTPSlot } from '@/components/ui/input-otp';

export default function MFAVerification() {
  const [otp, setOtp] = useState('');
  const [method, setMethod] = useState('app');
  const [cooldown, setCooldown] = useState(0);
  const [verified, setVerified] = useState(false);

  useEffect(() => {
    if (cooldown > 0) {
      const t = setTimeout(() => setCooldown(cooldown - 1), 1000);
      return () => clearTimeout(t);
    }
  }, [cooldown]);

  const handleVerify = () => {
    if (otp.length === 6) setVerified(true);
  };

  const handleResend = () => {
    setCooldown(30);
  };

  const methodIcons = { app: Smartphone, sms: MessageSquare, email: Mail };

  if (verified) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background p-6">
        <motion.div initial={{ opacity: 0, scale: 0.95 }} animate={{ opacity: 1, scale: 1 }} className="text-center">
          <div className="w-16 h-16 rounded-full bg-success/15 flex items-center justify-center mx-auto mb-4">
            <Shield className="w-8 h-8 text-success" />
          </div>
          <h2 className="text-xl font-display font-bold text-foreground mb-2">Verified!</h2>
          <p className="text-sm text-muted-foreground">MFA verification successful. Redirecting...</p>
        </motion.div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-background p-6">
      <motion.div initial={{ opacity: 0, y: 20 }} animate={{ opacity: 1, y: 0 }} className="w-full max-w-[400px]">
        <div className="flex items-center gap-3 mb-8">
          <div className="w-12 h-12 rounded-2xl gradient-primary flex items-center justify-center">
            <Shield className="w-6 h-6 text-primary-foreground" />
          </div>
          <div>
            <p className="font-display font-extrabold text-lg text-foreground">{APP_NAME}</p>
            <p className="text-xs text-muted-foreground">Two-Factor Authentication</p>
          </div>
        </div>

        <h2 className="text-2xl font-display font-extrabold text-foreground mb-2">Verify your identity</h2>
        <p className="text-muted-foreground text-sm mb-6">Enter the 6-digit code from your authenticator. Code expires in 10 minutes.</p>

        <div className="space-y-5">
          <div>
            <label className="text-sm font-semibold text-foreground mb-2 block">MFA Method</label>
            <Select value={method} onValueChange={setMethod}>
              <SelectTrigger className="h-12 rounded-xl">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="app">Authenticator App</SelectItem>
                <SelectItem value="sms">SMS</SelectItem>
                <SelectItem value="email">Email</SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div>
            <label className="text-sm font-semibold text-foreground mb-3 block">OTP Code</label>
            <div className="flex justify-center">
              <InputOTP maxLength={6} value={otp} onChange={setOtp}>
                <InputOTPGroup>
                  <InputOTPSlot index={0} />
                  <InputOTPSlot index={1} />
                  <InputOTPSlot index={2} />
                  <InputOTPSlot index={3} />
                  <InputOTPSlot index={4} />
                  <InputOTPSlot index={5} />
                </InputOTPGroup>
              </InputOTP>
            </div>
          </div>

          <Button onClick={handleVerify} disabled={otp.length !== 6} className="w-full h-12 rounded-xl font-bold gradient-primary text-primary-foreground">
            Verify Code
          </Button>

          <div className="text-center">
            <button onClick={handleResend} disabled={cooldown > 0} className="text-sm text-primary hover:text-primary/80 font-medium disabled:text-muted-foreground disabled:cursor-not-allowed">
              {cooldown > 0 ? `Resend code in ${cooldown}s` : 'Resend Code'}
            </button>
          </div>
        </div>
      </motion.div>
    </div>
  );
}
