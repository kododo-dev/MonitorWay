import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import ListItemIcon from '@mui/material/ListItemIcon';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import LinearProgress from '@mui/material/LinearProgress';
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward';
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined';
import { useTheme } from '@mui/material/styles';
import type { EntryRow } from '../api/api.model';
import { LEVEL_SHORT, levelColor, levelRowTint } from '../utils/levels';
import { firstLine, formatFullTime, formatRowTime } from '../utils/format';
import { memberColor, shortMember } from '../utils/members';
import { useI18n } from '../i18n/i18n';
import Highlight from './Highlight';

const ROW_HEIGHT = 24;
const TIME_WIDTH = 150;
const LEVEL_WIDTH = 44;
const CATEGORY_WIDTH = 240;

export interface RowAction {
  label: string;
  icon: React.ReactNode;
  onClick: () => void;
}

/** The rows in view: their ids, and their times for an overview in time space. */
export interface VisibleRange {
  first: number;
  last: number;
  firstTime: number | null;
  lastTime: number | null;
}

interface Props {
  rows: EntryRow[];
  version: number;
  hasOlder: boolean;
  loadingOlder: boolean;
  onLoadOlder: () => void;
  hasNewer: boolean;
  loadingNewer: boolean;
  onLoadNewer: () => void;
  /** Set when the view is centred on an entry (context or jump to time) instead of following the newest. */
  anchorId: number | null;
  selectedId: number | null;
  onSelect: (id: number) => void;
  highlight: RegExp | null;
  rowActions: (row: EntryRow) => RowAction[];
  /** Ids of the first and last row in view, for the overview bar. */
  onVisibleRange: (range: VisibleRange | null) => void;
  emptyText: string;
  compact: boolean;
}

/** Keeps the end of a long category name, which is the part that identifies the type. */
function shortCategory(category: string | null): string {
  if (!category || category.length <= 32) return category ?? '';
  const parts = category.split('.');
  let result = parts.pop()!;
  while (parts.length && result.length + parts[parts.length - 1].length < 30)
    result = `${parts.pop()}.${result}`;
  return `…${result}`;
}

const LogList = ({
  rows, version, hasOlder, loadingOlder, onLoadOlder, hasNewer, loadingNewer, onLoadNewer, anchorId,
  selectedId, onSelect, highlight, rowActions, onVisibleRange, emptyText, compact,
}: Props) => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const scrollRef = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  const previousRows = useRef(rows);
  const [isFollowing, setIsFollowing] = useState(true);
  const [newCount, setNewCount] = useState(0);
  const [menu, setMenu] = useState<{ x: number; y: number; row: EntryRow } | null>(null);
  const anchored = anchorId !== null;

  // TanStack Virtual returns functions React Compiler cannot memoize, so it skips this component; nothing to fix here.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 30,
  });

  const scrollToEnd = useCallback(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
    following.current = true;
    setIsFollowing(true);
    setNewCount(0);
  }, []);

  // A fresh load starts at the newest entries, or with the anchor in the middle of the view. Rows also change
  // between loads (pages, live entries); those changes are handled by the next effect, so this one acts only when
  // the version says the rows were replaced.
  const positionedVersion = useRef<number | null>(null);
  useLayoutEffect(() => {
    if (positionedVersion.current === version)
      return;
    positionedVersion.current = version;
    previousRows.current = rows;
    const el = scrollRef.current;
    const anchorIndex = anchorId === null ? -1 : rows.findIndex(r => r.id === anchorId);
    if (el && anchorIndex >= 0) {
      el.scrollTop = Math.max(0, anchorIndex * ROW_HEIGHT - el.clientHeight / 2 + ROW_HEIGHT / 2);
      following.current = false;
      setIsFollowing(false);
      setNewCount(0);
    } else {
      scrollToEnd();
    }
  }, [version, rows, anchorId, scrollToEnd]);

  // Keeps the viewport steady when rows are added or removed above it, and follows new rows at the end.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    const previous = previousRows.current;
    previousRows.current = rows;
    if (!el || previous === rows) return;

    if (following.current) {
      el.scrollTop = el.scrollHeight;
      return;
    }
    if (!previous.length || !rows.length) return;

    const oldFirst = previous[0].id;
    const newFirst = rows[0].id;
    if (newFirst < oldFirst) {
      const added = rows.findIndex(r => r.id === oldFirst);
      if (added > 0) el.scrollTop += added * ROW_HEIGHT;
    } else if (newFirst > oldFirst) {
      const removed = previous.findIndex(r => r.id === newFirst);
      if (removed > 0) el.scrollTop -= removed * ROW_HEIGHT;
    }

    if (anchored) return;
    const oldLast = previous[previous.length - 1].id;
    const appended = rows.length - 1 - rows.findIndex(r => r.id === oldLast);
    if (rows[rows.length - 1].id > oldLast && appended > 0) setNewCount(c => c + appended);
  }, [rows, anchored]);

  // Reported once per frame at most; scrolling fires far more often than the bar needs.
  const reportFrame = useRef(0);
  const reportVisible = useCallback(() => {
    cancelAnimationFrame(reportFrame.current);
    reportFrame.current = requestAnimationFrame(() => {
      const el = scrollRef.current;
      if (!el || rows.length === 0) {
        onVisibleRange(null);
        return;
      }
      const first = Math.min(rows.length - 1, Math.floor(el.scrollTop / ROW_HEIGHT));
      const last = Math.min(rows.length - 1, Math.ceil((el.scrollTop + el.clientHeight) / ROW_HEIGHT) - 1);
      const top = rows[first];
      const bottom = rows[Math.max(first, last)];
      onVisibleRange({
        first: top.id,
        last: bottom.id,
        firstTime: top.timestamp ? Date.parse(top.timestamp) : null,
        lastTime: bottom.timestamp ? Date.parse(bottom.timestamp) : null,
      });
    });
  }, [rows, onVisibleRange]);

  useEffect(() => {
    reportVisible();
    return () => cancelAnimationFrame(reportFrame.current);
  }, [reportVisible]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    reportVisible();
    const fromEnd = el.scrollHeight - el.scrollTop - el.clientHeight;
    // A centred view never follows the end: it grows by pages instead.
    const atEnd = !anchored && fromEnd < ROW_HEIGHT * 2;
    if (atEnd !== following.current) {
      following.current = atEnd;
      setIsFollowing(atEnd);
      if (atEnd) setNewCount(0);
    }
    if (el.scrollTop < ROW_HEIGHT * 20 && hasOlder && !loadingOlder) onLoadOlder();
    if (fromEnd < ROW_HEIGHT * 20 && hasNewer && !loadingNewer) onLoadNewer();
  }, [anchored, hasOlder, loadingOlder, onLoadOlder, hasNewer, loadingNewer, onLoadNewer, reportVisible]);

  // Short result sets do not scroll, so the scroll handler never asks for more rows.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el || el.scrollHeight > el.clientHeight) return;
    if (hasOlder && !loadingOlder) onLoadOlder();
    else if (hasNewer && !loadingNewer) onLoadNewer();
  }, [rows, hasOlder, loadingOlder, onLoadOlder, hasNewer, loadingNewer, onLoadNewer]);

  const border = theme.palette.divider;
  const headerCell = { px: 1, flexShrink: 0, color: theme.palette.text.secondary, fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em' } as const;

  return (
    <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', position: 'relative' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', height: 28, borderBottom: `1px solid ${border}`, flexShrink: 0 }}>
        <Box sx={{ ...headerCell, width: TIME_WIDTH }}>{t.time}</Box>
        <Box sx={{ ...headerCell, width: LEVEL_WIDTH }} />
        {!compact && <Box sx={{ ...headerCell, width: CATEGORY_WIDTH }}>{t.category}</Box>}
        <Box sx={{ ...headerCell, flex: 1 }}>{t.message}</Box>
      </Box>

      {loadingOlder && <LinearProgress sx={{ position: 'absolute', top: 28, left: 0, right: 0, height: 2, zIndex: 1 }} />}
      {loadingNewer && <LinearProgress sx={{ position: 'absolute', bottom: 0, left: 0, right: 0, height: 2, zIndex: 1 }} />}

      {/* The browser's own scroll anchoring would fight the compensation above and make the view drift. */}
      <Box ref={scrollRef} onScroll={onScroll} sx={{ flex: 1, overflow: 'auto', position: 'relative', overflowAnchor: 'none' }}>
        {rows.length === 0 ? (
          <Typography sx={{ p: 3, color: theme.palette.text.secondary, fontSize: '0.85rem' }}>{emptyText}</Typography>
        ) : (
          <Box sx={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
            {virtualizer.getVirtualItems().map(item => {
              const row = rows[item.index];
              const selected = row.id === selectedId;
              const isAnchor = row.id === anchorId;
              return (
                <Box
                  key={row.id}
                  data-testid="log-row"
                  data-id={row.id}
                  onClick={() => onSelect(row.id)}
                  onContextMenu={e => {
                    e.preventDefault();
                    setMenu({ x: e.clientX, y: e.clientY, row });
                  }}
                  sx={{
                    position: 'absolute', top: 0, left: 0, right: 0,
                    height: ROW_HEIGHT,
                    transform: `translateY(${item.start}px)`,
                    display: 'flex', alignItems: 'center',
                    fontSize: '0.8rem', lineHeight: `${ROW_HEIGHT}px`,
                    cursor: 'pointer',
                    whiteSpace: 'nowrap',
                    background: selected ? (isDark ? '#2f3b4a' : '#e3f2fd') : levelRowTint(row.level, isDark),
                    borderBottom: `1px solid ${isDark ? '#232323' : '#eee'}`,
                    boxShadow: isAnchor ? `inset 3px 0 0 ${theme.palette.primary.main}` : undefined,
                    '&:hover': { background: selected ? undefined : (isDark ? '#2a2a2a' : '#f0f0f0') },
                  }}
                >
                  <Tooltip title={formatFullTime(row.timestamp)} placement="top-start" enterDelay={600} disableInteractive>
                    <Box sx={{ width: TIME_WIDTH, px: 1, flexShrink: 0, color: theme.palette.text.secondary }}>
                      {formatRowTime(row.timestamp)}
                    </Box>
                  </Tooltip>
                  <Box sx={{ width: LEVEL_WIDTH, px: 1, flexShrink: 0, fontWeight: 700, color: levelColor(row.level, isDark) }}>
                    {LEVEL_SHORT[row.level]}
                  </Box>
                  {!compact && (
                    <Box sx={{ width: CATEGORY_WIDTH, px: 1, flexShrink: 0, overflow: 'hidden', textOverflow: 'ellipsis', color: theme.palette.text.secondary }}>
                      {shortCategory(row.category)}
                    </Box>
                  )}
                  <Box sx={{ flex: 1, minWidth: 0, px: 1, overflow: 'hidden', textOverflow: 'ellipsis', display: 'flex', alignItems: 'center', gap: 0.75 }}>
                    {row.member && (
                      <Tooltip title={row.member} placement="top-start" enterDelay={600} disableInteractive>
                        <Box
                          component="span"
                          sx={{
                            flexShrink: 0, px: 0.75, lineHeight: '16px', fontSize: '0.7rem', borderRadius: 1,
                            color: memberColor(row.member, isDark),
                            border: `1px solid ${memberColor(row.member, isDark)}`,
                          }}
                        >
                          {shortMember(row.member)}
                        </Box>
                      </Tooltip>
                    )}
                    {row.hasException && <BugReportOutlinedIcon sx={{ fontSize: 14, color: levelColor('Error', isDark), flexShrink: 0 }} />}
                    <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      <Highlight text={firstLine(row.message)} pattern={highlight} />
                    </Box>
                  </Box>
                </Box>
              );
            })}
          </Box>
        )}
      </Box>

      {!anchored && !isFollowing && rows.length > 0 && (
        <Button
          size="small"
          variant="contained"
          startIcon={<ArrowDownwardIcon sx={{ fontSize: 16 }} />}
          onClick={scrollToEnd}
          sx={{ position: 'absolute', right: 20, bottom: 16, textTransform: 'none', fontSize: '0.75rem' }}
        >
          {newCount > 0 ? t.newEntries(newCount) : t.jumpToLatest}
        </Button>
      )}

      <Menu
        open={menu !== null}
        onClose={() => setMenu(null)}
        anchorReference="anchorPosition"
        anchorPosition={menu ? { top: menu.y, left: menu.x } : undefined}
        slotProps={{ list: { dense: true } }}
      >
        {menu && rowActions(menu.row).map(action => (
          <MenuItem
            key={action.label}
            onClick={() => {
              setMenu(null);
              action.onClick();
            }}
            sx={{ fontSize: '0.8rem' }}
          >
            <ListItemIcon sx={{ '& svg': { fontSize: 18 } }}>{action.icon}</ListItemIcon>
            {action.label}
          </MenuItem>
        ))}
      </Menu>
    </Box>
  );
};

export default LogList;
