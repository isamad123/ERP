// Leave screens (slice S1.33a; FR-HRM-LVE-04 to -06, -16): My leave at `/time/leave`, Apply for leave at `/time/leave/apply` (also `?change=<id>` and `?draft=<id>`),
// and the request page at `/time/leave/requests/:id`, the link target of the approvals inbox.
import { Route, Routes } from 'react-router';
import ApplyScreen from './leave-apply';
import MyLeaveScreen from './leave-my';
import LeaveRequestPage from './leave-request';
import './leave-messages';

export default function LeaveScreen() {
  return (
    <Routes>
      <Route index element={<MyLeaveScreen />} />
      <Route path="apply" element={<ApplyScreen />} />
      <Route path="requests/:id" element={<LeaveRequestPage />} />
    </Routes>
  );
}
