'use client';

import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';

/** Renders release notes (setup/v<version>.md) as sanitized Markdown (no raw HTML scripts). */
export default function SafeMarkdown({ content }: { content: string }) {
  const text = content?.trim();
  if (!text) {
    return <p className="text-sm text-muted-foreground italic">No release notes provided.</p>;
  }

  return (
    <div className="prose prose-sm dark:prose-invert max-w-none prose-a:text-primary prose-pre:bg-muted prose-pre:text-foreground">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeSanitize]}
        components={{
          a: ({ href, children, ...props }) => (
            <a href={href} target="_blank" rel="noopener noreferrer" {...props}>
              {children}
            </a>
          ),
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}
