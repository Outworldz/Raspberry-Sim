#!perl.exe

# to start :
# plackup -l localhost:5000 Server.plx


$|=1;

use CGI::PSGI;

use strict;
use warnings;

use Template;
use Plack::Builder;
use Plack::Request;
use Plack::Response;
use Plack::App::File;

use Cwd;
my $dir = getcwd;


my $osname = $^O;
my $path = 'Raspberry-Sim';
if( $osname eq 'MSWin32' ){
	$path = 'OsGridWin';
}


my $home = sub {
	my $req = Plack::Request->new(shift);  
	
	my $contents = { Title => "Opensimulator on $osname",
					 HTML => "Welcome to Opensimulator on $osname!",
					};
	
	MakeHTML($req,'index.tt',$contents);
	
};


my $save = sub {
  
	my $req = Plack::Request->new(shift);  
	 
	my %INI;
    dbmopen(%INI,'INI',0666);
	my $content = { REGIONUUID 	=> $INI{UUID},
					 LocX 		=> $INI{LocX},
					 LocY 		=> $INI{LocY},
					 PORT 		=> $INI{Port},
					 SYSTEMIP 	=> $INI{SYSTEMIP},
					};
	dbmclose(%INI);
	
	use Template;
	my $tt = Template->new({
         INTERPOLATE => 0,
		 ENCODING => 'utf8',
		 ABSOLUTE => 1,
         ANYCASE => 1,
		 INCLUDE_PATH => 'include',
         ERROR      => 'error.tt',
      }) or die $Template::ERROR;
	
	my $output;
	
	$tt->process($dir .'/include/' . 'Regions.ini.example', $content, \$output);
	
	if (open (INI, ">$path/bin/Regions/Region.ini"))
	{
		print INI $output;
		close INI;
	}
	
	my $body = 'Setup Complete. You can start the system now' ;
	
	my $res = $req->new_response(200);
	$res->content_type('text/html');
	$res->body($body);
	$res->finalize;
	
};

my $config = sub {
	
	my $req = Plack::Request->new(shift);  
	my %INI;
    dbmopen(%INI,'INI',0666);
	use UUID::Tiny  ':legacy';
	
	my $UUID = $INI{UUID} || '';
	if (! is_UUID_string($UUID) ) {
		$UUID = uc(create_UUID_as_string(UUID_V1));
	}


	my $content = {	 RegionName => $INI{RegionName} || 'Choose a Name',
					 UUID 		=> $UUID,
					 LocX 		=> $INI{LocX} || GetFree('x'),
					 LocY 		=> $INI{LocY} || GetFree('x'),
					 PORT 		=> $INI{Port} || 9000,
					 SYSTEMIP 	=> $INI{SYSTEMIP} || GetPublicIP(),
				  };
	
	dbmclose(%INI);
	
	use Template;
	my $tt = Template->new({
         INTERPOLATE => 0,
		 ENCODING => 'utf8',
		 ABSOLUTE => 1,
         ANYCASE => 1,
		 INCLUDE_PATH => 'include',
         ERROR      => 'error.tt',
      }) or die $Template::ERROR;
	
	my $body;
	
	$tt->process($dir .'/setup.tt', $content, \$body);	
	my $res = $req->new_response(200);
	$res->content_type('text/html');
	$res->body($body);
	$res->finalize;
};



=pod
Needs to check the URL
http://map.osgrid.org/regioninfo.php?coords=x10010x9994x&user=00000000-0000-0000-0000-000000000000&scopeid=00000000-0000-0000-0000-000000000000
which returns this:

Region: AF Empty 22
Owner: Steve Franklin
Location: 10010 x 9994
=cut

sub GetFree {
	return int(rand(500))+19000;
}

sub MakeHTML {
	use Template;
	my $req = shift;
	my $page = shift;
	
	my $content= shift;
	
	my $tt = Template->new({
         INTERPOLATE => 0,
		 ENCODING => 'utf8',
		 ABSOLUTE => 1,
         ANYCASE => 1,
		 INCLUDE_PATH => 'include',
         ERROR      => 'error.tt',
      }) or die $Template::ERROR;
	
	my $output;
	use Cwd;
    my $dir = getcwd;
	
	$tt->process($dir .'/' . $page, $content, \$output);
	
	my $res = $req->new_response(200);
	$res->content_type('text/html');
	$res->body($output);
	$res->finalize;
}

sub GetPublicIP {
	use LWP::UserAgent;
	my $ua = LWP::UserAgent->new;
	$ua->agent("PiSim/1.0 ");

	# Create a request
	my $req = HTTP::Request->new(GET => 'http://www.Outworldz.com/cgi/publicip.plx');
	# Pass request to the user agent and get a response back
	my $res = $ua->request($req);

	# Check and get the outcome of the response
	if ($res->is_success) {
      return $res->content;
	}
	return;
}

builder {
	mount '/' 		=> $home,
	mount '/config' => $config,
    mount '/save'	=> $save,
};


#&$config;
