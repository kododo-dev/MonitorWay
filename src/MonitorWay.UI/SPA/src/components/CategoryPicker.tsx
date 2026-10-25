import { useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import InputAdornment from '@mui/material/InputAdornment';
import Popover from '@mui/material/Popover';
import TextField from '@mui/material/TextField';
import ToggleButton from '@mui/material/ToggleButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import CategoryOutlinedIcon from '@mui/icons-material/CategoryOutlined';
import SearchIcon from '@mui/icons-material/Search';
import { useTheme } from '@mui/material/styles';
import { fetchCategories } from '../api/api';
import { useI18n } from '../i18n/i18n';
import { isShown, onlyCategory, toggleCategory, type CategoryFilter } from '../utils/categories';
import { formatCount } from '../utils/format';

interface Props {
  sourceId: string;
  filter: CategoryFilter;
  onChange: (filter: CategoryFilter) => void;
}

/** Categories of the source with their counts, each with a checkbox: checked categories are shown. */
const CategoryPicker = ({ sourceId, filter, onChange }: Props) => {
  const theme = useTheme();
  const { t } = useI18n();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [categories, setCategories] = useState<{ name: string; count: number }[] | null>(null);
  const [search, setSearch] = useState('');

  const open = (target: HTMLElement) => {
    setAnchor(target);
    setSearch('');
    setCategories(null);
    fetchCategories(sourceId).then(setCategories).catch(() => setCategories([]));
  };

  const known = (categories ?? []).map(c => c.name);
  const shown = (categories ?? []).filter(c => c.name.toLowerCase().includes(search.toLowerCase()));
  const shownCount = known.filter(name => isShown(filter, name)).length;
  const row = { display: 'flex', alignItems: 'center', gap: 0.5, pl: 0.5, pr: 1.5, cursor: 'pointer' };

  return (
    <>
      <Tooltip title={filter ? `${t.categories}: ${t.categoriesActive}` : t.categories}>
        <ToggleButton value="categories" size="small" selected={filter !== null} onChange={e => open(e.currentTarget)} sx={{ px: 0.75, py: 0.4 }}>
          <CategoryOutlinedIcon sx={{ fontSize: 18 }} />
        </ToggleButton>
      </Tooltip>
      <Popover open={anchor !== null} anchorEl={anchor} onClose={() => setAnchor(null)} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}>
        <Box sx={{ width: 420, maxWidth: 'calc(100vw - 32px)', display: 'flex', flexDirection: 'column', maxHeight: 480 }}>
          <Box sx={{ p: 1.5, borderBottom: `1px solid ${theme.palette.divider}` }}>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder={t.categoriesFilter}
              slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon sx={{ fontSize: 16 }} /></InputAdornment> } }}
            />
          </Box>
          {categories !== null && categories.length > 0 && (
            <Box onClick={() => onChange(null)} sx={{ ...row, borderBottom: `1px solid ${theme.palette.divider}`, '&:hover': { background: theme.palette.action.hover } }}>
              <Checkbox size="small" checked={filter === null} indeterminate={filter !== null} sx={{ p: 0.5 }} />
              <Typography sx={{ flex: 1, fontSize: '0.78rem', fontWeight: 600 }}>{t.allCategories}</Typography>
              <Typography sx={{ fontSize: '0.72rem', color: 'text.secondary' }}>
                {t.categoriesShown(formatCount(shownCount), formatCount(known.length))}
              </Typography>
            </Box>
          )}
          <Box sx={{ overflow: 'auto', py: 0.5 }}>
            {categories === null && <Typography sx={{ p: 2, fontSize: '0.8rem', color: 'text.secondary' }}>{t.loading}</Typography>}
            {categories !== null && shown.length === 0 && (
              <Typography sx={{ p: 2, fontSize: '0.8rem', color: 'text.secondary' }}>{t.noCategories}</Typography>
            )}
            {shown.map(category => {
              const checked = isShown(filter, category.name);
              return (
                <Box key={category.name} onClick={() => onChange(toggleCategory(filter, category.name, known))} sx={{
                  ...row,
                  '&:hover': { background: theme.palette.action.hover },
                  '&:hover .only': { visibility: 'visible' },
                }}>
                  <Checkbox size="small" checked={checked} sx={{ p: 0.5 }} />
                  <Typography title={category.name} sx={{
                    flex: 1, minWidth: 0, fontSize: '0.78rem', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    // Long namespaces are cut on the left, where they share their beginning with the others.
                    direction: 'rtl', textAlign: 'left',
                    color: checked ? 'text.primary' : 'text.secondary',
                  }}>
                    {'‎'}{category.name}
                  </Typography>
                  <Button
                    className="only"
                    size="small"
                    onClick={e => {
                      e.stopPropagation();
                      onChange(onlyCategory(category.name));
                    }}
                    sx={{ visibility: 'hidden', '@media (hover: none)': { visibility: 'visible' }, minWidth: 0, px: 0.75, py: 0, fontSize: '0.7rem', textTransform: 'none' }}
                  >
                    {t.onlyThis}
                  </Button>
                  <Typography sx={{ fontSize: '0.72rem', color: 'text.secondary', flexShrink: 0 }}>{formatCount(category.count)}</Typography>
                </Box>
              );
            })}
          </Box>
        </Box>
      </Popover>
    </>
  );
};

export default CategoryPicker;
