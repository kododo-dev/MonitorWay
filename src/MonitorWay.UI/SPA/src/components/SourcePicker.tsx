import { useMemo, useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import InputAdornment from '@mui/material/InputAdornment';
import Popover from '@mui/material/Popover';
import TextField from '@mui/material/TextField';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import ArrowDropDownIcon from '@mui/icons-material/ArrowDropDown';
import DescriptionOutlinedIcon from '@mui/icons-material/DescriptionOutlined';
import LayersOutlinedIcon from '@mui/icons-material/LayersOutlined';
import MemoryIcon from '@mui/icons-material/Memory';
import SearchIcon from '@mui/icons-material/Search';
import { useTheme } from '@mui/material/styles';
import type { Source } from '../api/api.model';
import { useI18n } from '../i18n/i18n';
import { groupSources, selectionLabel, toggleGroup, toggleSource } from '../utils/sources';

interface Props {
  sources: Source[];
  /** Ids of the sources shown; several are merged by time. */
  value: string[];
  onChange: (ids: string[]) => void;
}

/**
 * The sources under their groups (a directory, a service), searchable by name and group. A click shows one source;
 * the checkboxes add sources, or a whole group, to show them together.
 */
const SourcePicker = ({ sources, value, onChange }: Props) => {
  const theme = useTheme();
  const { t } = useI18n();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [search, setSearch] = useState('');
  const ordered = useMemo(() => groupSources(sources), [sources]);

  const needle = search.trim().toLowerCase();
  const shown = needle
    ? ordered.filter(s => s.name.toLowerCase().includes(needle) || s.group?.toLowerCase().includes(needle))
    : ordered;
  const groups: { group: string | null; members: Source[] }[] = [];
  for (const source of shown) {
    const last = groups[groups.length - 1];
    if (last && last.group === (source.group ?? null)) last.members.push(source);
    else groups.push({ group: source.group ?? null, members: [source] });
  }

  const label = selectionLabel(value, sources);
  const single = value.length === 1 ? sources.find(s => s.id === value[0]) : undefined;
  const choose = (id: string) => {
    onChange([id]);
    setAnchor(null);
  };
  const row = { display: 'flex', alignItems: 'center', gap: 0.5, pl: 0.5, pr: 1.5, cursor: 'pointer', '&:hover': { background: theme.palette.action.hover } };

  return (
    <>
      <Tooltip title={single?.group ? `${single.group} / ${single.name}` : label}>
        <Button
          variant="outlined"
          color="inherit"
          aria-label={t.source}
          onClick={e => {
            setSearch('');
            setAnchor(e.currentTarget);
          }}
          endIcon={<ArrowDropDownIcon />}
          sx={{ minWidth: 180, maxWidth: 320, flexShrink: 0, justifyContent: 'space-between', textTransform: 'none', fontSize: '0.8rem', fontWeight: 400, borderColor: theme.palette.divider }}
        >
          <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1, overflow: 'hidden' }}>
            <KindIcon kind={value.length > 1 ? 'merged' : single?.kind} />
            <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</Box>
          </Box>
        </Button>
      </Tooltip>
      <Popover open={anchor !== null} anchorEl={anchor} onClose={() => setAnchor(null)} anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}>
        <Box sx={{ width: 380, maxWidth: 'calc(100vw - 32px)', display: 'flex', flexDirection: 'column', maxHeight: 520 }}>
          <Box sx={{ p: 1.5, borderBottom: `1px solid ${theme.palette.divider}` }}>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={search}
              onChange={e => setSearch(e.target.value)}
              onKeyDown={e => {
                if (e.key === 'Enter' && shown.length) choose(shown[0].id);
              }}
              placeholder={t.sourcesFilter}
              slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon sx={{ fontSize: 16 }} /></InputAdornment> } }}
            />
          </Box>
          <Box sx={{ overflow: 'auto', py: 0.5 }}>
            {shown.length === 0 && <Typography sx={{ p: 2, fontSize: '0.8rem', color: 'text.secondary' }}>{t.noSourcesFound}</Typography>}
            {groups.map(({ group, members }) => {
              const ids = members.map(s => s.id);
              const all = ids.every(id => value.includes(id));
              const some = !all && ids.some(id => value.includes(id));
              return (
                <Box key={group ?? ''}>
                  {group && (
                    <Tooltip title={members.length > 1 ? t.showGroupTogether : ''} placement="right">
                      <Box onClick={() => onChange(toggleGroup(value, ids))} sx={{ ...row, mt: 0.5 }}>
                        <Checkbox size="small" checked={all} indeterminate={some} sx={{ p: 0.5 }} />
                        <Typography sx={{ flex: 1, fontSize: '0.72rem', fontWeight: 600, color: 'text.secondary', wordBreak: 'break-all' }}>{group}</Typography>
                      </Box>
                    </Tooltip>
                  )}
                  {members.map(source => (
                    <Box key={source.id} onClick={() => choose(source.id)} sx={{ ...row, pl: group ? 2.5 : 0.5 }}>
                      <Checkbox
                        size="small"
                        checked={value.includes(source.id)}
                        onClick={e => {
                          e.stopPropagation();
                          onChange(toggleSource(value, source.id));
                        }}
                        sx={{ p: 0.5 }}
                      />
                      <KindIcon kind={source.kind} />
                      <Typography title={source.name} sx={{ flex: 1, minWidth: 0, fontSize: '0.8rem', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {source.name}
                      </Typography>
                    </Box>
                  ))}
                </Box>
              );
            })}
          </Box>
          <Typography sx={{ px: 1.5, py: 0.75, borderTop: `1px solid ${theme.palette.divider}`, fontSize: '0.7rem', color: 'text.secondary' }}>
            {t.sourcesHint}
          </Typography>
        </Box>
      </Popover>
    </>
  );
};

const KindIcon = ({ kind }: { kind?: string }) => {
  const sx = { fontSize: 16, flexShrink: 0 };
  if (kind === 'memory') return <MemoryIcon sx={sx} />;
  if (kind === 'merged') return <LayersOutlinedIcon sx={sx} />;
  return <DescriptionOutlinedIcon sx={sx} />;
};

export default SourcePicker;
