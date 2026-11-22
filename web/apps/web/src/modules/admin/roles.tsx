// The role screens (slice S1.13b Build items 6 and 7; FR-ADM-ROL-01, -02, -03, FR-ADM-NAV-03): roles list, permission
// catalogue, side-by-side comparison and the role record. Menu path /admin/roles.
import { Route, Routes } from 'react-router';
import { EngineProvider, type EngineApi } from '@erp/engine';
import { sendText } from '../../api/client';
import { Catalogue } from './roles-catalogue';
import { Compare } from './roles-compare';
import { RoleList } from './roles-list';
import { RoleRecordScreen } from './roles-record';
import './roles.css';

const api: EngineApi = { send: sendText };

export default function RolesScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<RoleList />} />
        <Route path="catalogue" element={<Catalogue />} />
        <Route path="compare" element={<Compare />} />
        <Route path=":id" element={<RoleRecordScreen />} />
      </Routes>
    </EngineProvider>
  );
}
