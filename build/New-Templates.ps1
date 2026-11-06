<#
  Regenerates the import templates that are embedded in Jps.Core (src\Jps.Core\Templates).
  Requires Excel on the build machine (end users never need this script).
  Run:  powershell -ExecutionPolicy Bypass -File build\New-Templates.ps1
#>
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\src\Jps.Core\Templates' | Resolve-Path

function New-Workbook([string]$path, [string]$dataSheet, [string[]]$headers, [string[][]]$instructions) {
    $xl = New-Object -ComObject Excel.Application
    $xl.Visible = $false; $xl.DisplayAlerts = $false
    try {
        $wb = $xl.Workbooks.Add()
        while ($wb.Worksheets.Count -lt 2) { [void]$wb.Worksheets.Add([Type]::Missing, $wb.Worksheets.Item($wb.Worksheets.Count)) }
        while ($wb.Worksheets.Count -gt 2) { $wb.Worksheets.Item($wb.Worksheets.Count).Delete() }

        # Data sheet must stay first: the tools read Sheets[1] starting at row 2.
        $ws = $wb.Worksheets.Item(1); $ws.Name = $dataSheet
        for ($c = 0; $c -lt $headers.Count; $c++) { $ws.Cells.Item(1, $c + 1).Value2 = $headers[$c] }
        $hdr = $ws.Range($ws.Cells.Item(1, 1), $ws.Cells.Item(1, $headers.Count))
        $hdr.Font.Bold = $true; $hdr.Interior.Color = 0xEAD9C6
        $ws.Range($ws.Cells.Item(2, 1), $ws.Cells.Item(5000, $headers.Count)).NumberFormat = '@'
        [void]$ws.Columns.AutoFit()
        for ($c = 1; $c -le $headers.Count; $c++) { if ($ws.Columns.Item($c).ColumnWidth -lt 14) { $ws.Columns.Item($c).ColumnWidth = 14 } }
        $ws.Activate(); $xl.ActiveWindow.SplitRow = 1; $xl.ActiveWindow.FreezePanes = $true

        $ins = $wb.Worksheets.Item(2); $ins.Name = 'Instructions'
        for ($r = 0; $r -lt $instructions.Count; $r++) {
            for ($c = 0; $c -lt $instructions[$r].Count; $c++) { $ins.Cells.Item($r + 1, $c + 1).Value2 = $instructions[$r][$c] }
        }
        $ins.Rows.Item(1).Font.Bold = $true; $ins.Rows.Item(1).Font.Size = 13
        $ins.Columns.Item(1).ColumnWidth = 22; $ins.Columns.Item(2).ColumnWidth = 95; $ins.Columns.Item(2).WrapText = $true

        $ws.Activate()
        if (Test-Path $path) { Remove-Item $path }
        $wb.SaveAs($path, 51)  # 51 = xlOpenXMLWorkbook (.xlsx)
        $wb.Close($false)
    } finally {
        $xl.Quit(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($xl)
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
}

# ---- Matter Closer: client code in column A, matter code in column B, data from row 2.
New-Workbook (Join-Path $out 'Matter Closer Template.xlsx') 'Matters' @('Client Code', 'Matter Code') @(
    ,@('Matter Closer template', '')
    ,@('What this does', 'Lists the matters you want closed. Each matter that has nothing outstanding gets the status Closed, the lock Disallow time and expenses and today''s close date. Put one matter per row on the Matters sheet, starting on row 2, and keep the heading row as it is.')
    ,@('Client Code', 'Column A, required. The client code as Juris shows it. If your client codes are numbers, leading zeros are added for you when "Add leading zeros" is ticked in the tool.')
    ,@('Matter Code', 'Column B, required. The matter code under that client, as Juris shows it.')
    ,@('Rows', 'Do not add or move columns, and keep the Matters sheet first. Rows with a blank Client Code or Matter Code are skipped.')
    ,@('What gets closed', 'Only matters with nothing outstanding are closed: no unbilled time or expenses (WIP), no unposted time, expenses, vouchers, cash receipts or trust adjustments, no open vouchers, bills or A/R, no prepaid or trust balance, not on an open prebill and not split-billed. The tool shows every row first, with the reason for any matter it can''t close.')
    ,@('Example', 'Client Code 001234, Matter Code 0001 = close matter 0001 of client 001234.')
    ,@('Example', 'Client Code 1234, Matter Code 1 = the same matter when "Add leading zeros" is ticked and your codes are 6 and 4 digits long.')
)

# ---- Matter Status and Lock Updater: same layout as Matter Closer (A = client, B = matter, from row 2).
New-Workbook (Join-Path $out 'Matter Status Lock Template.xlsx') 'Matters' @('Client Code', 'Matter Code') @(
    ,@('Matter Status and Lock template', '')
    ,@('What this does', 'Lists the matters whose Matter Status and/or lock you want to change. You choose the new status (Open, Closed or Final Bill Sent) and lock (Allow time and expenses, Disallow time, Disallow expenses or Disallow time and expenses) in the tool; they apply to every matter in the file.')
    ,@('Client Code', 'Column A, required. The client code as Juris shows it. Leading zeros are added for you when your client codes are numbers.')
    ,@('Matter Code', 'Column B, required. The matter code under that client, as Juris shows it.')
    ,@('Rows', 'One matter per row on the Matters sheet, starting on row 2. Do not add or move columns, and keep the Matters sheet first. If any row has a client or matter that isn''t in Juris, nothing is changed and the tool lists those rows.')
    ,@('Closing matters', 'A matter is closed only when it has nothing outstanding (no unbilled or unposted work, open bills, A/R, prepaid or trust balance, open prebill or split billing). Closed matters always get the lock Disallow time and expenses. Matters that can''t be closed are listed with the reason.')
    ,@('Example', 'Client Code 001234, Matter Code 0001 = change matter 0001 of client 001234.')
)

# ---- Update Bill Copy Settings: columns A-I by position, data from row 2. Blank cell = leave that setting unchanged.
# Column C was "Type" with C / A; the tool still accepts C and A (and any heading text: columns are read by position).
New-Workbook (Join-Path $out 'Bill Copy Settings Template.xlsx') 'Bill Copies' @('Client Code', 'Matter Code', 'Apply To', 'Number of Copies', 'Print Bill', 'Print A/R Statement', 'Email Format', 'Export Format', 'Comment') @(
    ,@('Bill Copy Settings template', '')
    ,@('What this does', 'Each row changes the bill copy settings of a client''s or a matter''s bill-tos. Every bill copy of each bill-to changes, and every matter that shares the bill-to changes too. Put one row per client or matter on the Bill Copies sheet, starting on row 2.')
    ,@('Client Code', 'Column A, required. The client code exactly as Juris shows it, including leading zeros.')
    ,@('Matter Code', 'Column B, optional. Fill it in to change only that matter''s bill-to (the matter must not be Closed).')
    ,@('Apply To', 'Column C. Used when Matter Code is blank, and then required: Client Bill-Tos = the client''s own (client-level) bill-tos; All Matters = the bill-tos of every matter of the client that isn''t Closed. Files made from the older template, with C or A in this column, still work (C = Client Bill-Tos, A = All Matters).')
    ,@('Number of Copies', 'Column D. A whole number from 0 to 32767.')
    ,@('Print Bill', 'Column E. Yes or No.')
    ,@('Print A/R Statement', 'Column F. Yes or No.')
    ,@('Email Format', 'Column G. None, XLS, HTML, Text, RTF or PDF.')
    ,@('Export Format', 'Column H. None, XLS, HTML, Text, RTF, PDF, TIFF or LEDES.')
    ,@('Comment', 'Column I. Up to 30 characters.')
    ,@('Blank cells', 'A blank cell in columns D to I leaves that setting as it is. Each row must fill in at least one of them.')
    ,@('Rows', 'Keep the columns in this order and the Bill Copies sheet first. Every row is checked before anything is changed, and you see the changes before they are applied.')
    ,@('Example', 'Client Code 001234, Matter Code blank, Apply To All Matters, Number of Copies 2, Email Format PDF = 2 copies and PDF email on the bill-tos of all of client 001234''s matters.')
    ,@('Example', 'Client Code 001234, Matter Code 0001, Print Bill No, Comment Email only = matter 0001''s bill-to stops printing bills and gets that comment; nothing else changes.')
)

# ---- Voucher Import: headers by name (any order), sheet "Vouchers" (the tool reads Sheet1, else the first sheet).
# Third-party exports (Chrome River, Concur) use the original 25 header names; the tool accepts those as well.
New-Workbook (Join-Path $out 'Voucher Import Template.xlsx') 'Vouchers' @('Row Type', 'Voucher Date', 'Vendor Code', 'PO Number', 'Invoice Number', 'Due Date', 'Invoice Date', 'Discount Date', 'Invoice Amount', 'Non-Discountable Amount', 'Voucher Reference', 'Separate Check (Yes/No)', 'A/P Account', 'GL Distribution Account', 'GL Amount', 'Trust Bank', 'Client Code', 'Matter Code', 'Expense Code', 'Task Code', 'Incurred By', 'Expense Units', 'Expense Amount', 'Expense Narrative', 'Expense Bill Note') @(
    ,@('Voucher Import template', '')
    ,@('What this does', 'Creates one new unposted Payment Voucher batch in Juris with the vouchers in this file. Nothing is posted: you post the batch in Juris. Put the rows on the Vouchers sheet starting on row 2 and keep the heading row (columns may be in any order). A .csv file with the same headings works too.')
    ,@('Row Type', 'Required on every row. A = starts an A/P (accounts payable) voucher. T = starts a trust voucher, paid from a trust bank account for one matter. G = a GL line of the A voucher above it. E = an expense line of the A voucher above it, charged to a client matter. Put the G and E rows directly below their A row; trust vouchers have no G or E rows.')
    ,@('Voucher Date', 'A and T rows, required. mm/dd/yyyy.')
    ,@('Vendor Code', 'Every row, required. The vendor code as Juris shows it. G and E rows repeat the vendor code of their voucher.')
    ,@('PO Number', 'A and T rows, optional. Up to 20 characters.')
    ,@('Invoice Number', 'Every row, required. Up to 20 characters. G and E rows repeat the invoice number of their voucher.')
    ,@('Due Date', 'A and T rows, optional. mm/dd/yyyy. Blank = the invoice date.')
    ,@('Invoice Date', 'A and T rows, required. mm/dd/yyyy.')
    ,@('Discount Date', 'A and T rows, optional. mm/dd/yyyy.')
    ,@('Invoice Amount', 'A and T rows, required and not zero. Up to 2 decimal places.')
    ,@('Non-Discountable Amount', 'A and T rows, optional. The part of the invoice no discount applies to.')
    ,@('Voucher Reference', 'A and T rows, required. The voucher''s reference (memo) text.')
    ,@('Separate Check (Yes/No)', 'A and T rows, optional. Yes to pay this voucher on its own check. Blank = No. Y and N also work.')
    ,@('A/P Account', 'A rows, optional. Blank = the vendor''s default A/P account.')
    ,@('GL Distribution Account', 'G rows, required. The GL account exactly as Juris shows it, with its separators (for example 6100-00).')
    ,@('GL Amount', 'G rows, required. The amount for that GL account.')
    ,@('Trust Bank', 'T rows, required. The trust bank account the voucher is paid from. The matter must have enough trust funds in it.')
    ,@('Client Code', 'E rows (the matter the expense is charged to) and T rows (the trust matter), required. As Juris shows it.')
    ,@('Matter Code', 'E and T rows, required. The matter code under that client. For E rows the matter must be Open and allow expenses.')
    ,@('Expense Code', 'E rows, required. A Juris expense code that has a rate in the standard expense schedule (STDR).')
    ,@('Task Code', 'E rows, optional (required when the matter requires task codes on expenses).')
    ,@('Incurred By', 'E rows, optional. The employee ID of the person who incurred the expense.')
    ,@('Expense Units', 'E rows. Fill in Expense Units, Expense Amount or both.')
    ,@('Expense Amount', 'E rows. Blank = Expense Units times the matter''s expense schedule rate.')
    ,@('Expense Narrative', 'E rows, optional. The expense description.')
    ,@('Expense Bill Note', 'E rows, optional. A note for the bill.')
    ,@('Balancing', 'The GL lines of an A voucher must add up to its Invoice Amount. When a voucher has expense lines, a GL line for their total is added for you on the vendor''s default distribution account (unless the G rows already add up to the invoice).')
    ,@('Duplicates', 'An invoice that already exists in Juris for the same vendor and invoice date is rejected, or imported with a suffix, as you choose in the tool. An invoice repeated in the file gets _2, _3 ... on its later copies.')
    ,@('Older column names', 'Files with the original headings are accepted too, so exports already set up for Chrome River, Concur and similar systems keep working: Type, VoucherDate, VendorCode, PONbr, InvoiceNbr, DueDate, InvoiceDate, DiscountDate, InvoiceAmt, NonDiscAmt, VchReference, SeparateCheck, APAcct, GLDistAcct, GLAmt, TrustBank, ExpClient, ExpMatter, ExpCode, ExpTaskCode, IncurredBy, ExpUnits, ExpAmount, ExpNarrative, ExpBillNote (same order as the columns here).')
    ,@('Example', 'Row 2: A, 11/01/2026, ACME, (blank), INV-1001, (blank), 11/01/2026, (blank), 500.00, (blank), Office supplies and copies, No = an A/P voucher for vendor ACME, invoice INV-1001, for 500.00.')
    ,@('Example', 'Row 3: G, vendor ACME, invoice INV-1001, GL Distribution Account 6100-00, GL Amount 300.00. Row 4: E, vendor ACME, invoice INV-1001, Client Code 001234, Matter Code 0001, Expense Code COPY, Expense Amount 200.00. The 200.00 expense gets its own GL line automatically, so the voucher balances at 500.00.')
)

# ---- Juris Core Budget Importer: matter fee (Task) and expense (Expense) budget lines, sheet "Sheet1", headers by name.
# The tool also still reads files made from the original template (Client, Matter, Task_Type, TaskExp, Task_Code, Task_Desc, Hours, Amount, MatSysNbr).
New-Workbook (Join-Path $out 'Juris Core Budget Importer Template.xlsx') 'Sheet1' @('Client Code', 'Matter Code', 'Fee or Expense', 'Task Code', 'Expense Code', 'Activity Code', 'Timekeeper ID', 'Personnel Type', 'Budget Phase', 'Budgeted Hours', 'Budgeted Amount', 'Description') @(
    ,@('Juris Core Budget Importer template', '')
    ,@('What this does', 'Each row is one budget line for one matter. Every matter in the file has its WHOLE fee and expense budget replaced by its rows in the file. Matters not in the file are not touched. Put the rows on Sheet1 starting on row 2 and keep the heading row as it is.')
    ,@('Client Code', 'Required. The client code exactly as Juris shows it, including leading zeros.')
    ,@('Matter Code', 'Required. The matter code exactly as Juris shows it.')
    ,@('Fee or Expense', 'Required. Fee for a fee (time) budget line, Expense for an expense budget line.')
    ,@('Task Code', 'Optional. A Juris task code. Leave blank for a line that covers any task code.')
    ,@('Expense Code', 'Expense rows only, optional. A Juris expense code. Leave blank for a line that covers any expense code.')
    ,@('Activity Code', 'Fee rows only, optional. A Juris activity code. Leave blank for any activity code.')
    ,@('Timekeeper ID', 'Fee rows only, optional. The timekeeper''s employee ID. Leave blank for any timekeeper.')
    ,@('Personnel Type', 'Fee rows only, optional. A Juris personnel type. Leave blank for any personnel type. If you also fill in Timekeeper ID, it must be that timekeeper''s personnel type.')
    ,@('Budget Phase', 'Optional. A whole number from 0 to 999. Leave blank to use the phase you choose in the tool (normally 0).')
    ,@('Budgeted Hours', 'Fee rows: the budgeted hours (blank = 0). Ignored on Expense rows.')
    ,@('Budgeted Amount', 'The budgeted amount (blank = 0). Rows with 0 hours and 0 amount are still imported as budget lines.')
    ,@('Description', 'Optional. For your own reference; it is not imported.')
    ,@('How posted work finds its line', 'When time or an expense is posted or billed, Juris adds it to a budget line of that matter in the entry''s own Budget Phase. It picks the line that fits best: Task Code counts most, then Activity Code, then Timekeeper, then Personnel Type (for expenses: Task Code, then Expense Code). A blank code on a line fits anything; a code that differs from the entry''s never fits. If no line fits, Juris adds a catch-all line (all codes blank) in that phase and posts there.')
    ,@('Phases', 'Each matter has a current Budget Phase, chosen in the tool. New time and expense entries start in that phase. If the tool sets "Require phase on transactions" to Yes, every phase you use, and the current phase, needs at least one Fee row and one Expense row (blank codes and a 0 amount are fine).')
    ,@('Actuals already posted', 'Hours and amounts already posted to a matter''s current budget lines are kept: they move to the new line that the same work would post to. If no new line would take them, nothing is imported and the tool lists those lines.')
    ,@('Duplicate rows', 'Two rows for the same matter with the same Fee or Expense, Budget Phase and codes are not allowed. Combine them into one row.')
    ,@('Example', 'Client 001234, Matter 0001, Fee, Task Code L110, Budget Phase 1, Budgeted Hours 20, Budgeted Amount 5000 = a fee budget of 20 hours / 5,000.00 for task L110 in phase 1, for any activity and timekeeper.')
    ,@('Example', 'Client 001234, Matter 0001, Expense, Expense Code COPY, Budgeted Amount 250 (Budget Phase blank) = an expense budget of 250.00 for copies in the phase chosen in the tool.')
    ,@('Example', 'Client 001234, Matter 0001, Fee, all codes blank, Budget Phase 1, Budgeted Amount 0 = a catch-all fee line for phase 1 that takes any time no other line fits.')
)

Get-ChildItem $out | Select-Object Name, Length
