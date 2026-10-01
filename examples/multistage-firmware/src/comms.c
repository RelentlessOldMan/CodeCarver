void uart_write(int b);
void send_status(int v) { uart_write(v & 0xff); }
void uart_write(int b)  { (void)b; }
