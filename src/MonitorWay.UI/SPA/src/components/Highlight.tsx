import React from 'react';
import Box from '@mui/material/Box';
import { useTheme } from '@mui/material/styles';

interface Props {
  text: string;
  /** A global (`g`) expression; it is not modified. */
  pattern: RegExp | null;
}

/** Marks the parts of `text` matching `pattern`. */
const Highlight = ({ text, pattern }: Props) => {
  const theme = useTheme();
  if (!pattern) return <>{text}</>;

  const parts: React.ReactNode[] = [];
  let last = 0;
  for (const match of text.matchAll(pattern)) {
    if (match[0].length === 0) continue;
    if (match.index > last) parts.push(text.slice(last, match.index));
    parts.push(
      <Box
        component="mark"
        key={match.index}
        sx={{
          background: theme.palette.mode === 'dark' ? 'rgba(255, 213, 79, 0.35)' : 'rgba(255, 213, 79, 0.7)',
          color: 'inherit',
          borderRadius: '2px',
          px: '1px',
        }}
      >
        {match[0]}
      </Box>,
    );
    last = match.index + match[0].length;
  }
  if (last < text.length) parts.push(text.slice(last));
  return <>{parts}</>;
};

export default Highlight;
