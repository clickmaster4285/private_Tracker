'use client';

export default function EmptyState({ icon: Icon, text }: { icon: React.ElementType; text: string }) {
  return (
    <div className="bg-card rounded-xl border border-border py-12 flex flex-col items-center gap-3 text-center px-6">
      <div className="w-12 h-12 rounded-xl bg-muted flex items-center justify-center">
        <Icon className="w-6 h-6 text-muted-foreground" />
      </div>
      <p className="text-sm text-muted-foreground">{text}</p>
    </div>
  );
}
