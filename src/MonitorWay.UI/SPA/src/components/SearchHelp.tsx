import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import { useI18n } from '../i18n/i18n';

/** The search syntax as a table of examples, for the tooltip next to a search field. */
const SearchHelp = ({ rows }: { rows: readonly (readonly string[])[] }) => {
  const { t } = useI18n();
  return (
    <Box sx={{ p: 0.5 }}>
      <Typography sx={{ fontSize: '0.75rem', fontWeight: 700, mb: 0.5 }}>{t.searchHelpTitle}</Typography>
      <Box component="table" sx={{ borderCollapse: 'collapse', fontSize: '0.72rem' }}>
        <tbody>
          {rows.map(([example, meaning]) => (
            <tr key={example}>
              <Box component="td" sx={{ pr: 2, py: 0.15, fontFamily: "'IBM Plex Mono', monospace", whiteSpace: 'nowrap' }}>{example}</Box>
              <Box component="td" sx={{ py: 0.15, opacity: 0.8 }}>{meaning}</Box>
            </tr>
          ))}
        </tbody>
      </Box>
    </Box>
  );
};

export default SearchHelp;
