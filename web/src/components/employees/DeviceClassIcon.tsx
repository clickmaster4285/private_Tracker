'use client';

import { Camera, HardDrive, Headphones, Keyboard, Usb, Wifi } from 'lucide-react';

export default function DeviceClassIcon({ deviceClass }: { deviceClass: string }) {
  const cls = deviceClass.toLowerCase();
  const icons: [string[], React.ElementType][] = [
    [['keyboard', 'input', 'mouse', 'hid'], Keyboard],
    [['audio', 'headphone', 'sound'], Headphones],
    [['display', 'monitor', 'camera'], Camera],
    [['storage', 'disk'], HardDrive],
    [['network', 'wifi', 'ethernet'], Wifi],
  ];
  const match = icons.find(([keys]) => keys.some(k => cls.includes(k)));
  const Icon = match ? match[1] : Usb;
  return <Icon className="w-5 h-5 text-info" />;
}
