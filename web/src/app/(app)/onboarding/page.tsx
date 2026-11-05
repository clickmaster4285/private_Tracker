'use client';

import { useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { Building2, Users, Download, FileText, Shield, Check, ChevronRight, ChevronLeft, Upload, Plus, Trash2 } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';

const steps = [
  { id: 'company', label: 'Company Setup', icon: Building2 },
  { id: 'departments', label: 'Departments', icon: Users },
  { id: 'agent', label: 'Agent Deploy', icon: Download },
  { id: 'invite', label: 'Invite Users', icon: Users },
  { id: 'consent', label: 'Consent', icon: Shield },
];

const industries = ['Technology', 'Finance', 'Healthcare', 'BPO', 'Retail', 'Manufacturing', 'Education', 'Government', 'Other'];
const companySizes = ['1-10', '11-50', '51-200', '201-500', '501-1000', '1000+'];
const dateFormats = ['DD/MM/YYYY', 'MM/DD/YYYY', 'YYYY-MM-DD'];
const currencies = ['USD', 'EUR', 'GBP', 'INR', 'AUD', 'CAD', 'SGD', 'AED'];
const plans = ['Starter', 'Growth', 'Business', 'Enterprise'];

export default function OnboardingPage() {
  const [currentStep, setCurrentStep] = useState(0);
  const [companyData, setCompanyData] = useState({
    name: '', industry: '', size: '', country: '', timezone: '', dateFormat: 'DD/MM/YYYY', currency: 'USD',
    primaryColor: '#7C3AED', billingEmail: '', billingPlan: 'Growth', billingPeriod: 'monthly',
  });
  const [departments, setDepartments] = useState([
    { id: '1', name: 'Engineering', code: 'ENG', manager: '' },
    { id: '2', name: 'Design', code: 'DES', manager: '' },
  ]);
  const [teams, setTeams] = useState([{ id: '1', name: 'Frontend', department: 'Engineering', lead: '' }]);
  const [agentConfig, setAgentConfig] = useState({ os: 'windows', method: 'manual', updateMode: 'auto' });
  const [invites, setInvites] = useState([{ name: '', email: '', role: 'Employee', department: 'Engineering', team: '', jobTitle: '' }]);
  const [policy, setPolicy] = useState({
    name: 'Standard Monitoring Policy', 
    scopes: { appUsage: true, screenshots: true, gps: false },
    monitoringStart: '09:00', monitoringEnd: '18:00',
    weekendMonitoring: false, employeeVisibility: true,
  });

  const next = () => setCurrentStep(Math.min(steps.length - 1, currentStep + 1));
  const prev = () => setCurrentStep(Math.max(0, currentStep - 1));

  const addDepartment = () => setDepartments([...departments, { id: String(Date.now()), name: '', code: '', manager: '' }]);
  const removeDepartment = (id: string) => setDepartments(departments.filter(d => d.id !== id));
  const addInvite = () => setInvites([...invites, { name: '', email: '', role: 'Employee', department: 'Engineering', team: '', jobTitle: '' }]);
  const removeInvite = (i: number) => setInvites(invites.filter((_, idx) => idx !== i));

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Stepper */}
      <div className="bg-card rounded-xl border border-border p-6">
        <div className="flex items-center justify-between mb-2">
          {steps.map((step, i) => {
            const Icon = step.icon;
            const active = i === currentStep;
            const done = i < currentStep;
            return (
              <div key={step.id} className="flex items-center gap-2 flex-1">
                <button onClick={() => setCurrentStep(i)} className={`flex items-center gap-2 px-3 py-2 rounded-lg text-sm font-medium transition-all
                  ${active ? 'bg-primary/10 text-primary' : done ? 'text-success' : 'text-muted-foreground'}`}>
                  <div className={`w-8 h-8 rounded-full flex items-center justify-center text-xs font-bold
                    ${active ? 'gradient-primary text-primary-foreground' : done ? 'bg-success text-success-foreground' : 'bg-muted text-muted-foreground'}`}>
                    {done ? <Check className="w-4 h-4" /> : i + 1}
                  </div>
                  <span className="hidden lg:inline">{step.label}</span>
                </button>
                {i < steps.length - 1 && <div className={`flex-1 h-0.5 mx-2 ${i < currentStep ? 'bg-success' : 'bg-border'}`} />}
              </div>
            );
          })}
        </div>
      </div>

      <AnimatePresence mode="wait">
        <motion.div key={currentStep} initial={{ opacity: 0, x: 20 }} animate={{ opacity: 1, x: 0 }} exit={{ opacity: 0, x: -20 }} className="bg-card rounded-xl border border-border p-6">
          {/* Step 0: Company Setup */}
          {currentStep === 0 && (
            <div className="space-y-6">
              <h3 className="font-display font-bold text-lg text-foreground">Organization Profile</h3>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Company Name *</label>
                  <Input value={companyData.name} onChange={e => setCompanyData({...companyData, name: e.target.value})} placeholder="Your company name" className="h-10 rounded-lg" />
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Industry *</label>
                  <Select value={companyData.industry} onValueChange={v => setCompanyData({...companyData, industry: v})}>
                    <SelectTrigger className="h-10"><SelectValue placeholder="Select industry" /></SelectTrigger>
                    <SelectContent>{industries.map(i => <SelectItem key={i} value={i}>{i}</SelectItem>)}</SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Company Size *</label>
                  <Select value={companyData.size} onValueChange={v => setCompanyData({...companyData, size: v})}>
                    <SelectTrigger className="h-10"><SelectValue placeholder="Select size" /></SelectTrigger>
                    <SelectContent>{companySizes.map(s => <SelectItem key={s} value={s}>{s} employees</SelectItem>)}</SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Country / Region *</label>
                  <Input value={companyData.country} onChange={e => setCompanyData({...companyData, country: e.target.value})} placeholder="e.g. United States" className="h-10 rounded-lg" />
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Time Zone *</label>
                  <Input value={companyData.timezone} onChange={e => setCompanyData({...companyData, timezone: e.target.value})} placeholder="e.g. America/New_York" className="h-10 rounded-lg" />
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Date Format *</label>
                  <Select value={companyData.dateFormat} onValueChange={v => setCompanyData({...companyData, dateFormat: v})}>
                    <SelectTrigger className="h-10"><SelectValue /></SelectTrigger>
                    <SelectContent>{dateFormats.map(f => <SelectItem key={f} value={f}>{f}</SelectItem>)}</SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Currency *</label>
                  <Select value={companyData.currency} onValueChange={v => setCompanyData({...companyData, currency: v})}>
                    <SelectTrigger className="h-10"><SelectValue /></SelectTrigger>
                    <SelectContent>{currencies.map(c => <SelectItem key={c} value={c}>{c}</SelectItem>)}</SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Primary Color</label>
                  <div className="flex items-center gap-2">
                    <input type="color" value={companyData.primaryColor} onChange={e => setCompanyData({...companyData, primaryColor: e.target.value})} className="w-10 h-10 rounded border-0 cursor-pointer" />
                    <Input value={companyData.primaryColor} onChange={e => setCompanyData({...companyData, primaryColor: e.target.value})} className="h-10 rounded-lg flex-1" />
                  </div>
                </div>
              </div>

              <div className="border-t border-border pt-6">
                <h4 className="font-display font-bold text-foreground mb-4">Billing & Subscription</h4>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div>
                    <label className="text-sm font-semibold text-foreground mb-1.5 block">Subscription Plan *</label>
                    <Select value={companyData.billingPlan} onValueChange={v => setCompanyData({...companyData, billingPlan: v})}>
                      <SelectTrigger className="h-10"><SelectValue /></SelectTrigger>
                      <SelectContent>{plans.map(p => <SelectItem key={p} value={p}>{p}</SelectItem>)}</SelectContent>
                    </Select>
                  </div>
                  <div>
                    <label className="text-sm font-semibold text-foreground mb-1.5 block">Billing Email *</label>
                    <Input type="email" value={companyData.billingEmail} onChange={e => setCompanyData({...companyData, billingEmail: e.target.value})} placeholder="billing@company.com" className="h-10 rounded-lg" />
                  </div>
                  <div>
                    <label className="text-sm font-semibold text-foreground mb-1.5 block">Billing Period *</label>
                    <div className="flex gap-3">
                      {['monthly', 'annual'].map(p => (
                        <button key={p} onClick={() => setCompanyData({...companyData, billingPeriod: p})}
                          className={`flex-1 py-2.5 rounded-lg text-sm font-medium border transition-all
                            ${companyData.billingPeriod === p ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground'}`}>
                          {p === 'monthly' ? 'Monthly' : 'Annual (15% off)'}
                        </button>
                      ))}
                    </div>
                  </div>
                </div>
              </div>
            </div>
          )}

          {/* Step 1: Departments & Teams */}
          {currentStep === 1 && (
            <div className="space-y-6">
              <div className="flex items-center justify-between">
                <h3 className="font-display font-bold text-lg text-foreground">Department Setup</h3>
                <Button onClick={addDepartment} size="sm" variant="outline" className="gap-1"><Plus className="w-4 h-4" /> Add Department</Button>
              </div>
              <div className="space-y-3">
                {departments.map(dept => (
                  <div key={dept.id} className="flex items-center gap-3 p-3 bg-muted/50 rounded-lg">
                    <Input value={dept.name} onChange={e => setDepartments(departments.map(d => d.id === dept.id ? {...d, name: e.target.value} : d))} placeholder="Department name" className="h-9 flex-1" />
                    <Input value={dept.code} onChange={e => setDepartments(departments.map(d => d.id === dept.id ? {...d, code: e.target.value} : d))} placeholder="Code" className="h-9 w-24" />
                    <Input value={dept.manager} onChange={e => setDepartments(departments.map(d => d.id === dept.id ? {...d, manager: e.target.value} : d))} placeholder="Manager email" className="h-9 flex-1" />
                    <button onClick={() => removeDepartment(dept.id)} className="p-1.5 rounded hover:bg-destructive/10 text-muted-foreground hover:text-destructive"><Trash2 className="w-4 h-4" /></button>
                  </div>
                ))}
              </div>

              <div className="border-t border-border pt-6">
                <h4 className="font-display font-bold text-foreground mb-4">Team Setup</h4>
                <div className="space-y-3">
                  {teams.map(team => (
                    <div key={team.id} className="flex items-center gap-3 p-3 bg-muted/50 rounded-lg">
                      <Input value={team.name} onChange={e => setTeams(teams.map(t => t.id === team.id ? {...t, name: e.target.value} : t))} placeholder="Team name" className="h-9 flex-1" />
                      <Select value={team.department} onValueChange={v => setTeams(teams.map(t => t.id === team.id ? {...t, department: v} : t))}>
                        <SelectTrigger className="h-9 w-40"><SelectValue /></SelectTrigger>
                        <SelectContent>{departments.filter(d => d.name).map(d => <SelectItem key={d.id} value={d.name}>{d.name}</SelectItem>)}</SelectContent>
                      </Select>
                      <Input value={team.lead} onChange={e => setTeams(teams.map(t => t.id === team.id ? {...t, lead: e.target.value} : t))} placeholder="Team lead email" className="h-9 flex-1" />
                    </div>
                  ))}
                  <Button onClick={() => setTeams([...teams, { id: String(Date.now()), name: '', department: '', lead: '' }])} size="sm" variant="outline" className="gap-1"><Plus className="w-4 h-4" /> Add Team</Button>
                </div>
              </div>
            </div>
          )}

          {/* Step 2: Agent Deploy */}
          {currentStep === 2 && (
            <div className="space-y-6">
              <h3 className="font-display font-bold text-lg text-foreground">Install Agent</h3>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Agent Token</label>
                  <div className="flex items-center gap-2">
                    <Input value="agt_k7x9m2p4q8r1s5t3" readOnly className="h-10 rounded-lg font-mono text-xs" />
                    <Button size="sm" variant="outline">Copy</Button>
                  </div>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">OS Platform *</label>
                  <Select value={agentConfig.os} onValueChange={v => setAgentConfig({...agentConfig, os: v})}>
                    <SelectTrigger className="h-10"><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="windows">Windows</SelectItem>
                      <SelectItem value="macos">macOS</SelectItem>
                      <SelectItem value="linux">Linux</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Install Method *</label>
                  <div className="flex gap-2 flex-wrap">
                    {['manual', 'gpo', 'mdm', 'script'].map(m => (
                      <button key={m} onClick={() => setAgentConfig({...agentConfig, method: m})}
                        className={`px-3 py-2 rounded-lg text-sm font-medium border transition-all
                          ${agentConfig.method === m ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground'}`}>
                        {m.toUpperCase()}
                      </button>
                    ))}
                  </div>
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Agent Update Mode</label>
                  <Select value={agentConfig.updateMode} onValueChange={v => setAgentConfig({...agentConfig, updateMode: v})}>
                    <SelectTrigger className="h-10"><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="auto">Auto (Recommended)</SelectItem>
                      <SelectItem value="manual">Manual</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              </div>

              <div className="border-t border-border pt-6">
                <h4 className="font-display font-bold text-foreground mb-4">Connected Devices</h4>
                <div className="bg-muted/50 rounded-xl p-4">
                  <table className="w-full text-sm">
                    <thead><tr className="text-muted-foreground"><th className="text-left py-2">Device</th><th className="text-left py-2">OS</th><th className="text-left py-2">Version</th><th className="text-left py-2">Status</th><th className="text-left py-2">Last Seen</th></tr></thead>
                    <tbody>
                      <tr className="border-t border-border"><td className="py-2 text-foreground">DESKTOP-A7K2M</td><td className="py-2">Windows 11</td><td className="py-2">v2.4.1</td><td className="py-2"><span className="px-2 py-0.5 rounded-full text-xs bg-success/15 text-success">Online</span></td><td className="py-2 text-muted-foreground">Just now</td></tr>
                      <tr className="border-t border-border"><td className="py-2 text-foreground">MacBook-Pro-J</td><td className="py-2">macOS 14.2</td><td className="py-2">v2.4.0</td><td className="py-2"><span className="px-2 py-0.5 rounded-full text-xs bg-warning/15 text-warning">Pending</span></td><td className="py-2 text-muted-foreground">2 hrs ago</td></tr>
                      <tr className="border-t border-border"><td className="py-2 text-foreground">ubuntu-srv-01</td><td className="py-2">Ubuntu 22.04</td><td className="py-2">v2.3.8</td><td className="py-2"><span className="px-2 py-0.5 rounded-full text-xs bg-destructive/15 text-destructive">Offline</span></td><td className="py-2 text-muted-foreground">1 day ago</td></tr>
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          )}

          {/* Step 3: Invite Users */}
          {currentStep === 3 && (
            <div className="space-y-6">
              <div className="flex items-center justify-between">
                <h3 className="font-display font-bold text-lg text-foreground">Invite Users</h3>
                <div className="flex gap-2">
                  <Button size="sm" variant="outline" className="gap-1"><Upload className="w-4 h-4" /> Bulk CSV Import</Button>
                  <Button onClick={addInvite} size="sm" variant="outline" className="gap-1"><Plus className="w-4 h-4" /> Add</Button>
                </div>
              </div>
              <div className="space-y-3">
                {invites.map((inv, i) => (
                  <div key={i} className="grid grid-cols-1 md:grid-cols-6 gap-2 p-3 bg-muted/50 rounded-lg items-end">
                    <div><label className="text-xs text-muted-foreground">Name *</label><Input value={inv.name} onChange={e => { const u = [...invites]; u[i].name = e.target.value; setInvites(u); }} placeholder="Full name" className="h-9" /></div>
                    <div><label className="text-xs text-muted-foreground">Email *</label><Input value={inv.email} onChange={e => { const u = [...invites]; u[i].email = e.target.value; setInvites(u); }} placeholder="Email" className="h-9" /></div>
                    <div><label className="text-xs text-muted-foreground">Role *</label>
                      <Select value={inv.role} onValueChange={v => { const u = [...invites]; u[i].role = v; setInvites(u); }}>
                        <SelectTrigger className="h-9"><SelectValue /></SelectTrigger>
                        <SelectContent><SelectItem value="Employee">Employee</SelectItem><SelectItem value="Manager">Manager</SelectItem><SelectItem value="HR Admin">HR Admin</SelectItem><SelectItem value="IT Admin">IT Admin</SelectItem></SelectContent>
                      </Select>
                    </div>
                    <div><label className="text-xs text-muted-foreground">Department *</label>
                      <Select value={inv.department} onValueChange={v => { const u = [...invites]; u[i].department = v; setInvites(u); }}>
                        <SelectTrigger className="h-9"><SelectValue /></SelectTrigger>
                        <SelectContent>{departments.filter(d => d.name).map(d => <SelectItem key={d.id} value={d.name}>{d.name}</SelectItem>)}</SelectContent>
                      </Select>
                    </div>
                    <div><label className="text-xs text-muted-foreground">Job Title</label><Input value={inv.jobTitle} onChange={e => { const u = [...invites]; u[i].jobTitle = e.target.value; setInvites(u); }} placeholder="Title" className="h-9" /></div>
                    <div className="flex items-end"><button onClick={() => removeInvite(i)} className="p-2 rounded hover:bg-destructive/10 text-muted-foreground hover:text-destructive"><Trash2 className="w-4 h-4" /></button></div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Step 4: Consent */}
          {currentStep === 4 && (
            <div className="space-y-6">
              <h3 className="font-display font-bold text-lg text-foreground">Monitoring Policy & Consent</h3>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Policy Name *</label>
                  <Input value={policy.name} onChange={e => setPolicy({...policy, name: e.target.value})} className="h-10 rounded-lg" />
                </div>
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Policy Version</label>
                  <Input value="v1.0" readOnly className="h-10 rounded-lg bg-muted" />
                </div>
              </div>

              <div>
                <label className="text-sm font-semibold text-foreground mb-2 block">Monitoring Scope *</label>
                <div className="flex flex-wrap gap-3">
                  {Object.entries(policy.scopes).map(([key, val]) => (
                    <label key={key} className={`flex items-center gap-2 px-3 py-2 rounded-lg border cursor-pointer transition-all
                      ${val ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground'}`}>
                      <input type="checkbox" checked={val} onChange={e => setPolicy({...policy, scopes: {...policy.scopes, [key]: e.target.checked}})} className="sr-only" />
                      <div className={`w-4 h-4 rounded border flex items-center justify-center ${val ? 'bg-primary border-primary' : 'border-muted-foreground'}`}>
                        {val && <Check className="w-3 h-3 text-primary-foreground" />}
                      </div>
                      <span className="text-sm font-medium capitalize">{key.replace(/([A-Z])/g, ' $1')}</span>
                    </label>
                  ))}
                </div>
              </div>

              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="text-sm font-semibold text-foreground mb-1.5 block">Monitoring Hours *</label>
                  <div className="flex items-center gap-2">
                    <Input type="time" value={policy.monitoringStart} onChange={e => setPolicy({...policy, monitoringStart: e.target.value})} className="h-10" />
                    <span className="text-muted-foreground">to</span>
                    <Input type="time" value={policy.monitoringEnd} onChange={e => setPolicy({...policy, monitoringEnd: e.target.value})} className="h-10" />
                  </div>
                </div>
              </div>

              <div className="space-y-3">
                <div className="flex items-center justify-between p-3 bg-muted/50 rounded-lg">
                  <div><p className="text-sm font-medium text-foreground">Weekend Monitoring</p><p className="text-xs text-muted-foreground">Track employee activity on weekends</p></div>
                  <Switch checked={policy.weekendMonitoring} onCheckedChange={v => setPolicy({...policy, weekendMonitoring: v})} />
                </div>
                <div className="flex items-center justify-between p-3 bg-muted/50 rounded-lg">
                  <div><p className="text-sm font-medium text-foreground">Employee Visibility</p><p className="text-xs text-muted-foreground">Allow employees to see their own monitoring data</p></div>
                  <Switch checked={policy.employeeVisibility} onCheckedChange={v => setPolicy({...policy, employeeVisibility: v})} />
                </div>
              </div>

              <div className="border-t border-border pt-6">
                <h4 className="font-display font-bold text-foreground mb-2">Consent Agreement Preview</h4>
                <div className="bg-muted/50 rounded-lg p-4 text-sm text-muted-foreground max-h-40 overflow-y-auto">
                  <p>By signing below, I acknowledge that I have read and understand the "{policy.name}" and consent to the monitoring practices described therein. I understand that my employer will monitor: {Object.entries(policy.scopes).filter(([,v]) => v).map(([k]) => k.replace(/([A-Z])/g, ' $1')).join(', ')}.</p>
                  <p className="mt-2">Monitoring will occur during working hours ({policy.monitoringStart} – {policy.monitoringEnd}){policy.weekendMonitoring ? ', including weekends' : ''}.</p>
                </div>
              </div>
            </div>
          )}
        </motion.div>
      </AnimatePresence>

      {/* Navigation */}
      <div className="flex items-center justify-between">
        <Button onClick={prev} disabled={currentStep === 0} variant="outline" className="gap-1">
          <ChevronLeft className="w-4 h-4" /> Previous
        </Button>
        <span className="text-sm text-muted-foreground">Step {currentStep + 1} of {steps.length}</span>
        <Button onClick={next} disabled={currentStep === steps.length - 1} className="gap-1 gradient-primary text-primary-foreground">
          {currentStep === steps.length - 1 ? 'Complete Setup' : 'Next'} <ChevronRight className="w-4 h-4" />
        </Button>
      </div>
    </div>
  );
}
